// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO.Compression;
using System.Security;
using ZErrorCode = System.IO.Pipelines.Compression.ZLibNative.ErrorCode;
using ZFlushCode = System.IO.Pipelines.Compression.ZLibNative.FlushCode;

namespace System.IO.Pipelines.Compression
{
    /// <summary>
    /// Provides a wrapper around the ZLib compression API
    /// </summary>
    internal sealed class Deflater : IDisposable
    {
        private ZLibNative.ZLibStreamHandle _zlibStream;
        private bool _isDisposed;
        private const int minWindowBits = -15;              // WindowBits must be between -8..-15 to write no header, 8..15 for a
        private const int maxWindowBits = 31;               // zlib header, or 24..31 for a GZip header

        // Note, DeflateStream or the deflater do not try to be thread safe.
        // The lock is just used to make writing to unmanaged structures atomic to make sure
        // that they do not get inconsistent fields that may lead to an unmanaged memory violation.
        // To prevent *managed* buffer corruption or other weird behaviour users need to synchronise
        // on the stream explicitly.
        private readonly object _syncLock = new object();

        public int AvailableInput => (int)_zlibStream.AvailIn;

        #region exposed members

        internal Deflater(CompressionLevel compressionLevel, int windowBits)
        {
            Debug.Assert(windowBits >= minWindowBits && windowBits <= maxWindowBits);
            ZLibNative.CompressionLevel zlibCompressionLevel;
            int memLevel;

            switch (compressionLevel)
            {
                // See the note in ZLibNative.CompressionLevel for the recommended combinations.

                case CompressionLevel.Optimal:
                    zlibCompressionLevel = ZLibNative.CompressionLevel.DefaultCompression;
                    memLevel = ZLibNative.Deflate_DefaultMemLevel;
                    break;

                case CompressionLevel.Fastest:
                    zlibCompressionLevel = ZLibNative.CompressionLevel.BestSpeed;
                    memLevel = ZLibNative.Deflate_DefaultMemLevel;
                    break;

                case CompressionLevel.NoCompression:
                    zlibCompressionLevel = ZLibNative.CompressionLevel.NoCompression;
                    memLevel = ZLibNative.Deflate_NoCompressionMemLevel;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(compressionLevel));
            }

            ZLibNative.CompressionStrategy strategy = ZLibNative.CompressionStrategy.DefaultStrategy;

            DeflateInit(zlibCompressionLevel, windowBits, memLevel, strategy);
        }

        ~Deflater()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        [SecuritySafeCritical]
        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                    _zlibStream.Dispose();

                _isDisposed = true;
            }
        }

        public bool NeedsInput()
        {
            return 0 == _zlibStream.AvailIn;
        }

        internal void SetInput(IntPtr buffer, int count)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");

            if (0 == count)
                return;

            lock (_syncLock)
            {
                _zlibStream.NextIn = buffer;
                _zlibStream.AvailIn = (uint)count;
            }
        }

        public int ReadDeflateOutput(IntPtr buffer, int count)
        {
            Contract.Ensures(Contract.Result<int>() >= 0 && Contract.Result<int>() <= count);

            Debug.Assert(!NeedsInput(), "GetDeflateOutput should only be called after providing input");

            try
            {
                int bytesRead;
                ReadDeflateOutput(buffer, count, ZFlushCode.NoFlush, out bytesRead);
                return bytesRead;
            }
            finally
            {
                // Before returning, make sure to release input buffer if necessary:
                if (0 == _zlibStream.AvailIn)
                    DeallocateInputBufferHandle();
            }
        }

        private unsafe ZErrorCode ReadDeflateOutput(IntPtr buffer, int count, ZFlushCode flushCode, out int bytesRead)
        {
            lock (_syncLock)
            {
                _zlibStream.NextOut = buffer;
                _zlibStream.AvailOut = (uint)count;

                ZErrorCode errC = Deflate(flushCode);
                bytesRead = count - (int)_zlibStream.AvailOut;

                return errC;
            }
        }

        internal bool Finish(IntPtr buffer, int count, out int bytesRead)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");

            // Note: we require that NeedsInput() == true, i.e. that 0 == _zlibStream.AvailIn.
            // If there is still input left we should never be getting here; instead we
            // should be calling GetDeflateOutput.

            ZErrorCode errC = ReadDeflateOutput(buffer, count, ZFlushCode.Finish, out bytesRead);
            return errC == ZErrorCode.StreamEnd;
        }

        /// <summary>
        /// Returns true if there was something to flush. Otherwise False.
        /// </summary>
        internal bool Flush(IntPtr buffer, int count, out int bytesRead)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");

            // Note: we require that NeedsInput() == true, i.e. that 0 == _zlibStream.AvailIn.
            // If there is still input left we should never be getting here; instead we
            // should be calling GetDeflateOutput.

            return ReadDeflateOutput(buffer, count, ZFlushCode.SyncFlush, out bytesRead) == ZErrorCode.Ok;
        }

        #endregion


        #region helpers & native call wrappers

        private void DeallocateInputBufferHandle()
        {
            lock (_syncLock)
            {
                _zlibStream.AvailIn = 0;
                _zlibStream.NextIn = ZLibNative.ZNullPtr;
            }
        }

        [SecuritySafeCritical]
        private void DeflateInit(ZLibNative.CompressionLevel compressionLevel, int windowBits, int memLevel,
                                 ZLibNative.CompressionStrategy strategy)
        {
            ZErrorCode errC;
            try
            {
                errC = ZLibNative.CreateZLibStreamForDeflate(out _zlibStream, compressionLevel,
                                                             windowBits, memLevel, strategy);
            }
            catch (Exception cause)
            {
                throw new ZLibException("SR.ZLibErrorDLLLoadError", cause);
            }

            switch (errC)
            {
                case ZErrorCode.Ok:
                    return;

                case ZErrorCode.MemError:
                    throw new ZLibException("SR.ZLibErrorNotEnoughMemory", "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                case ZErrorCode.VersionError:
                    throw new ZLibException("SR.ZLibErrorVersionMismatch", "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                case ZErrorCode.StreamError:
                    throw new ZLibException("SR.ZLibErrorIncorrectInitParameters", "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                default:
                    throw new ZLibException("SR.ZLibErrorUnexpected", "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());
            }
        }


        [SecuritySafeCritical]
        private ZErrorCode Deflate(ZFlushCode flushCode)
        {
            ZErrorCode errC;
            try
            {
                errC = _zlibStream.Deflate(flushCode);
            }
            catch (Exception cause)
            {
                throw new ZLibException("SR.ZLibErrorDLLLoadError", cause);
            }

            switch (errC)
            {
                case ZErrorCode.Ok:
                case ZErrorCode.StreamEnd:
                    return errC;

                case ZErrorCode.BufError:
                    return errC;  // This is a recoverable error

                case ZErrorCode.StreamError:
                    throw new ZLibException("SR.ZLibErrorInconsistentStream", "deflate", (int)errC, _zlibStream.GetErrorMessage());

                default:
                    throw new ZLibException("SR.ZLibErrorUnexpected", "deflate", (int)errC, _zlibStream.GetErrorMessage());
            }
        }
        #endregion

    }
}
