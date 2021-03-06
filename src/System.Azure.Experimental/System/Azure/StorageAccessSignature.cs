﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Binary.Base64;
using System.Buffers;
using System.Buffers.Cryptography;
using System.Text;

namespace System.Azure.Authentication
{
    public static class StorageAccessSignature
    {
        public static bool TryWrite(Span<byte> output, Sha256 hash, string verb, string canonicalizedResource, DateTime utc, out int bytesWritten)
        {
            int written, consumed;
            bytesWritten = 0;

            if (verb.Equals("GET", StringComparison.Ordinal))
            {
                if (output.Length < 3)
                {
                    bytesWritten = 0;
                    return false;
                }
                s_GET.CopyTo(output);
                bytesWritten += s_GET.Length;
            }
            else
            {
                if (Encodings.Utf16.ToUtf8(verb.AsReadOnlySpan().AsBytes(), output, out consumed, out written) != OperationStatus.Done)
                {
                    bytesWritten = 0;
                    return false;
                }

                output[written] = (byte)'\n';
                bytesWritten += written + 1;
            }

            var free = output.Slice(bytesWritten);
            s_emptyHeaders.CopyTo(free);
            bytesWritten += s_emptyHeaders.Length;

            free = output.Slice(bytesWritten);
            if (!Utf8Formatter.TryFormat(utc, free, out written, 'R'))
            {
                bytesWritten = 0;
                return false;
            }
            free[written] = (byte)'\n';
            bytesWritten += written + 1;
            free = output.Slice(bytesWritten);

            if (Encodings.Utf16.ToUtf8(canonicalizedResource.AsReadOnlySpan().AsBytes(), free, out consumed, out written) != OperationStatus.Done)
            {
                bytesWritten = 0;
                return false;
            }
            bytesWritten += written;

            var formatted = output.Slice(0, bytesWritten);

            hash.Append(formatted);
            hash.GetHash(output.Slice(0, hash.OutputSize));

            if (Base64.BytesToUtf8InPlace(output, hash.OutputSize, out written) != OperationStatus.Done)
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = written;
            return true;
        }

        static readonly byte[] s_GET = Encoding.UTF8.GetBytes("GET\n");

        static readonly byte[] s_emptyHeaders = Encoding.UTF8.GetBytes("\n\n\n\n\n\n\n\n\n\n\nx-ms-date:");
    }
}
