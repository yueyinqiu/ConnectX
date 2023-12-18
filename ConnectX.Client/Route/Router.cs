﻿using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ConnectX.Client.Interfaces;
using ConnectX.Client.Managers;
using ConnectX.Client.Models;
using ConnectX.Client.P2P;
using ConnectX.Client.Route.Packet;
using ConnectX.Shared.Helpers;
using ConsoleTables;
using Hive.Both.General.Dispatchers;
using Hive.Common.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHelper = ConnectX.Shared.Helpers.TaskHelper;

namespace ConnectX.Client.Route;

public class Router : BackgroundService
{
    private int _currentPeerCount;
    
    private readonly IServerLinkHolder _serverLinkHolder;
    private readonly PeerManager _peerManager;
    private readonly RouteTable _routeTable;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    
    private readonly ConcurrentDictionary<Guid, PingChecker> _pingCheckers = new();
    
    public Router(
        IServerLinkHolder serverLinkHolder,
        PeerManager peerManager,
        RouteTable routeTable,
        IServiceProvider serviceProvider,
        ILogger<Router> logger)
    {
        _serverLinkHolder = serverLinkHolder;
        _peerManager = peerManager;
        _routeTable = routeTable;
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        peerManager.OnPeerAdded += OnPeerAdded;
        peerManager.OnPeerRemoved += OnPeerRemoved;
    }
    
    public event Action<P2PPacket>? OnDelivery;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ROUTER] Starting router...");

        await TaskHelper.WaitUntilAsync(
            () => _serverLinkHolder is { IsConnected: true, IsSignedIn: true },
            stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[ROUTER] Router stopped because server link holder is not connected or signed in, now the app will exit");
            return;
        }
        
        _logger.LogInformation("[ROUTER] Router started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckLinkStateAsync();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
        
        _logger.LogInformation("[ROUTER] Router stopped");
    }

    private void OnPeerAdded(Guid id, Peer peer)
    {
        Interlocked.Increment(ref _currentPeerCount);
        
        peer.DirectLink.Dispatcher.AddHandler<P2PPacket>(OnP2PPacketReceived);
        peer.DirectLink.Dispatcher.AddHandler<LinkStatePacket>(OnLinkStatePacketReceived);
        peer.DirectLink.Dispatcher.AddHandler<P2PTransmitErrorPacket>(OnP2PTransmitErrorPacketReceived);
        
        var pingChecker = ActivatorUtilities.CreateInstance<PingChecker>(
            _serviceProvider,
            _serverLinkHolder.UserId,
            peer.Id,
            peer.DirectLink);
        
        _pingCheckers.AddOrUpdate(peer.Id, pingChecker, (_, _) => pingChecker);
        
        if (_routeTable.GetForwardInterface(id) == Guid.Empty)
            _routeTable.ForceAdd(id, id);

        _logger.LogInformation(
            "[ROUTER] Peer {PeerId} added, current peer count: {PearCount}",
            peer.Id, _currentPeerCount);

        CheckLinkStateAsync().CatchException();
    }
    
    private void OnPeerRemoved(Guid id, Peer peer)
    {
        Interlocked.Decrement(ref _currentPeerCount);
     
        peer.DirectLink.Dispatcher.RemoveHandler<P2PPacket>(OnP2PPacketReceived);
        peer.DirectLink.Dispatcher.RemoveHandler<LinkStatePacket>(OnLinkStatePacketReceived);
        peer.DirectLink.Dispatcher.RemoveHandler<P2PTransmitErrorPacket>(OnP2PTransmitErrorPacketReceived);
        
        _pingCheckers.TryRemove(peer.Id, out _);

        _logger.LogInformation(
            "[ROUTER] Peer {PeerId} removed, current peer count: {PearCount}",
            peer.Id, _currentPeerCount);

        var selfLinkState = _routeTable.GetSelfLinkState();
        if (selfLinkState != null)
        {
            for (var i = 0; i < selfLinkState.Interfaces.Length; i++)
            {
                if (selfLinkState.Interfaces[i] != id)
                    continue;

                selfLinkState.Costs[i] = int.MaxValue;

                break;
            }

            _routeTable.Update(selfLinkState);
        }

        CheckLinkStateAsync().CatchException();
    }
    
    private void OnP2PTransmitErrorPacketReceived(MessageContext<P2PTransmitErrorPacket> ctx)
    {
        var packet = ctx.Message;
        
        _logger.LogWarning(
            "[ROUTER] P2P transmit error: {Error}, from {From}, to {To}, original to {OriginalTo}",
            packet.Error, packet.From, packet.To, packet.OriginalTo);
    }
    
    private void OnP2PPacketReceived(MessageContext<P2PPacket> ctx)
    {
        _logger.LogTrace(
            "[ROUTER] P2P packet received from {RemoteEndPoint}",
            ctx.FromSession.RemoteEndPoint);

        var packet = ctx.Message;
        
        if (packet.To == _serverLinkHolder.UserId)
        {
            OnDelivery?.Invoke(packet);
        }
        else
        {
            packet.Ttl--;
            if (packet.Ttl == 0)
            {
                var error = new P2PTransmitErrorPacket
                {
                    Error = P2PTransmitError.TransmitExpired,
                    From = _serverLinkHolder.UserId,
                    To = packet.From,
                    OriginalTo = packet.To,
                    Payload = packet.Payload,
                    Ttl = 32
                };
                Send(error);
                
                return;
            }
            
            Send(packet);
        }
    }

    private void OnLinkStatePacketReceived(MessageContext<LinkStatePacket> ctx)
    {
        var linkState = ctx.Message;
        
        if (linkState.Source == _serverLinkHolder.UserId)
            return;

        _logger.LogDebug(
            "[ROUTER] Link state received from {RemoteEndPoint}",
            ctx.FromSession.RemoteEndPoint);

        linkState.Ttl--;
        if (linkState.Ttl == 0)
        {
            var error = new P2PTransmitErrorPacket
            {
                Error = P2PTransmitError.TransmitExpired,
                From = _serverLinkHolder.UserId,
                To = linkState.Source,
                Ttl = 32
            };
            Send(error);
                
            return;
        }
        
        _routeTable.Update(linkState);

        foreach (var (_, peer) in _peerManager)
            if (peer.DirectLink.Session != ctx.FromSession)
            {
                peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, linkState).Forget();
                _logger.LogDebug(
                    "[ROUTER] Link state forwarded to {RemoteEndPoint}",
                    peer.RemoteIpe);
            }
    }

    public void Send(Guid id, ReadOnlyMemory<byte> payload)
    {
        Send(new P2PPacket
        {
            From = _serverLinkHolder.UserId,
            To = id,
            Payload = payload,
            Ttl = 32
        });
        
        _logger.LogTrace(
            "[ROUTER] P2P packet sent to {RemoteEndPoint}",
            _peerManager[id].RemoteIpe);
    }
    
    public void Send(RouteLayerPacket packet)
    {
        var interfaceId = _routeTable.GetForwardInterface(packet.To);
        if (_peerManager.HasLink(interfaceId))
        {
            var peer = _peerManager[interfaceId];

            if (peer.IsConnected)
                peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, packet).Forget();
            else
                _logger.LogDebug(
                    "[ROUTER] {LinkId} is not connected",
                    interfaceId);
        }
        else
        {
            if (_peerManager.HasLink(packet.To))
            {
                var peer = _peerManager[packet.To];
                peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, packet).Forget();
            }
            else
                _logger.LogDebug(
                    "[ROUTER] {LinkId} is not reachable",
                    interfaceId);
        }
    }

    private async Task CheckLinkStateAsync()
    {
        _logger.LogInformation(
            "[ROUTER] Check link state, current peer count: {PeerCount}",
            _currentPeerCount);
        
        var interfaces = new List<Guid>();
        var states = new List<int>();
        var pingTasks = new List<(Guid, Task<int>)>();
        var ipMappings = new Dictionary<Guid, IPEndPoint>();
        
        lock (_peerManager)
        {
            foreach (var (key, peer) in _peerManager)
            {
                var ping = _pingCheckers[peer.Id];
                pingTasks.Add((key, ping.CheckPingAsync()));
                ipMappings.Add(key, peer.RemoteIpe);
                
                _logger.LogTrace(
                    "[ROUTER] Check link state to {RemoteEndPoint}",
                    peer.RemoteIpe);
            }
        }

        foreach (var (key, ping) in pingTasks)
        {
            interfaces.Add(key);
            var pingResult = await ping;
            states.Add(pingResult);
            
            _logger.LogTrace(
                "[ROUTER] Link state to {RemoteEndPoint} is {PingResult}",
                ipMappings[key], pingResult);
        }

        var linkState = new LinkStatePacket
        {
            Costs = states.ToArray(),
            Interfaces = interfaces.ToArray(),
            Source = _serverLinkHolder.UserId,
            Timestamp = DateTime.UtcNow.Ticks,
            Ttl = 32
        };
        
        _logger.LogInformation("Link state checking done");

        LogLinkState(linkState);
        BroadcastLinkState(linkState);
        _routeTable.Update(linkState);
    }
    
    private void BroadcastLinkState(LinkStatePacket linkStatePacket)
    {
        _logger.LogInformation(
            "[ROUTER] Broadcast link state to all peers, current peer count: {PeerCount}",
            _currentPeerCount);
        
        foreach (var (_, peer) in _peerManager)
        {
            peer.DirectLink.Dispatcher.SendAsync(peer.DirectLink.Session, linkStatePacket).Forget();
            _logger.LogTrace(
                "[ROUTER] Broadcast link state to {RemoteEndPoint}",
                peer.RemoteIpe);
        }
    }

    private void LogLinkState(LinkStatePacket linkState)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("Link state:");
        sb.AppendLine($"Source: {linkState.Source}");
        sb.AppendLine($"Timestamp: {linkState.Timestamp}");
        sb.AppendLine($"Ttl: {linkState.Ttl}");
        
        _logger.LogDebug(sb.ToString());

        if (linkState.Interfaces.Length > 0 ||
            linkState.Costs.Length > 0)
        {
            var table = new ConsoleTable("Interfaces", "Costs");
            
            for (var i = 0; i < linkState.Interfaces.Length; i++)
                table.AddRow(linkState.Interfaces[i], $"{linkState.Costs[i]}ms");
            
            table.Write();
            _logger.LogInformation("[ROUTER] Link state table printed");
        }
    }
}