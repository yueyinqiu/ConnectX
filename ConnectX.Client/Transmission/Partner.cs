﻿using ConnectX.Client.Transmission.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Transmission;

public class Partner
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly Guid _partnerId;

    private readonly Guid _selfId;
    private readonly IServiceProvider _serviceProvider;
    private bool _isLastTimeConnected;
    private PingChecker<Guid>? _pingChecker;

    public Partner(
        Guid selfId,
        Guid partnerId,
        ConnectionBase connection,
        IHostApplicationLifetime lifetime,
        IServiceProvider serviceProvider,
        ILogger<Partner> logger)
    {
        Connection = connection;

        _selfId = selfId;
        _partnerId = partnerId;
        _lifetime = lifetime;
        _serviceProvider = serviceProvider;
        _logger = logger;

        Hive.Common.Shared.Helpers.TaskHelper.FireAndForget(KeepConnectAsync);
    }

    public ConnectionBase Connection { get; }
    public int Latency { get; private set; }

    public event Action<Partner>? OnDisconnected;
    public event Action<Partner>? OnConnected;

    private async Task KeepConnectAsync()
    {
        while (!_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (!Connection.IsConnected)
            {
                if (_isLastTimeConnected)
                {
                    _logger.LogDisconnectedWithPartnerId(_partnerId);

                    OnDisconnected?.Invoke(this);

                    _isLastTimeConnected = false;
                    _pingChecker = null;
                }

                if (await Connection.ConnectAsync())
                {
                    _logger.LogConnectedWithPartnerId(_partnerId);

                    _isLastTimeConnected = true;

                    OnConnected?.Invoke(this);
                }
            }
            else
            {
                if (_isLastTimeConnected == false)
                {
                    _logger.LogConnectedWithPartnerId(_partnerId);

                    _isLastTimeConnected = true;

                    OnConnected?.Invoke(this);
                }

                _pingChecker ??= ActivatorUtilities.CreateInstance<PingChecker<Guid>>(
                    _serviceProvider,
                    _selfId,
                    _partnerId,
                    Connection);

                Latency = await _pingChecker.CheckPingAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.ApplicationStopping);
        }
    }

    public void Disconnect()
    {
        Connection.Disconnect();
    }
}

internal static partial class PartnerLoggers
{
    [LoggerMessage(LogLevel.Debug, "[PARTNER] Disconnected with {PartnerId}")]
    public static partial void LogDisconnectedWithPartnerId(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Debug, "[PARTNER] Connected with {PartnerId}")]
    public static partial void LogConnectedWithPartnerId(this ILogger logger, Guid partnerId);
}