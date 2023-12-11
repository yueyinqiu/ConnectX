﻿using ConnectX.Shared.Interfaces;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Shared.Messages.Group;

[MessageDefine]
[MemoryPackable]
public partial class JoinGroup : IRequireAssignedUserId
{
    public required Guid UserId { get; init; }
    public required Guid GroupId { get; init; }
    public string? RoomPassword { get; init; }
}