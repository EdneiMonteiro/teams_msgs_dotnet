// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// MessageId determinístico para dedup nativa do Service Bus (Standard):
//   {jobId}:{md5hex(rowKey)}:{repeatIndex}
// Idêntico ao do repo original (TS), garantindo idempotência sem a tabela
// `sentmarks`.

using System.Security.Cryptography;
using System.Text;

namespace TeamsMsgs.Shared.Messaging;

public static class ServiceBusMessageId
{
    public static string Compute(string jobId, string rowKey, int repeatIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(rowKey);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(rowKey));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{jobId}:{hex}:{repeatIndex}";
    }
}
