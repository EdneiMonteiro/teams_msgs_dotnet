// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Shared.Queueing;

public sealed record QueueMessageBody(string JobId, string RefJson, string RowKey, int RepeatIndex);
