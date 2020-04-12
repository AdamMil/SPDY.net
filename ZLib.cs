using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace SPDY
{
	#region ZlibCodec
	/// <summary>Implements a codec for compressing and decompressing data with zlib compression.</summary>
	sealed class ZlibCodec
	{
		/// <summary>Initializes a new codec to compress data.</summary>
		/// <param name="level">The compression level, from 0 (no compression) to 9 (best), or -1 to use the standard setting. The
		/// default is -1.
		/// </param>
		/// <param name="windowBits">The base-2 logarithm of the compression window size (e.g. 10 represents 2^10 = 1024 bytes). The
		/// default is 15, representing 2^15 = 32768 bytes.
		/// </param>
		public ZlibCodec(int level = -1, int windowBits = 15)
		{
			if((uint)(level+1) > 10) throw new ArgumentOutOfRangeException(nameof(level)); // verify -1 <= level < 10
			if(windowBits < 8 || windowBits > 15) throw new ArgumentOutOfRangeException(nameof(windowBits));
			Mode = CompressionMode.Compress;
			codec.InitializeDeflate((Ionic.Zlib.CompressionLevel)(level < 0 ? 6 : level), windowBits);
		}

		/// <summary>Initializes a new codec for compressing or decompressing data.</summary>
		public ZlibCodec(System.IO.Compression.CompressionMode mode)
		{
			if(mode != CompressionMode.Compress && mode != CompressionMode.Decompress) throw new ArgumentOutOfRangeException();
			Mode = mode;
			if(mode == CompressionMode.Compress) codec.InitializeDeflate();
			else codec.InitializeInflate();
		}

		/// <summary>The <see cref="CompressionMode"/>, either <see cref="CompressionMode.Compress"/> or
		/// <see cref="CompressionMode.Decompress"/>.
		/// </summary>
		public CompressionMode Mode { get; private set; }

		/// <summary>Resets the state of the codec. This is more efficient than creating a new codec and can be used to set a new
		/// dictionary or to recover if an error occurs due to invalid data.
		/// </summary>
		public void Reset()
		{
			if(Mode == CompressionMode.Compress) codec.ResetDeflate();
			else CheckResult((Result)codec.InitializeInflate());
			dictionary = null;
			used = false;
		}

		/// <summary>Sets the initial dictionary, which can be used to prime the codec with byte strings that will occur in the input and
		/// thereby reduce the size of compressed data. (The most commonly occurring byte strings should go last.) The decompressor must
		/// use an identical dictionary.
		/// </summary>
		public unsafe void SetDictionary(byte[] dictionary)
		{
			if(dictionary == null) throw new ArgumentNullException();
			AssertUsable();
			if(used) throw new InvalidOperationException("The dictionary can only be set before the codec is used for the first time.");
			if(Mode == CompressionMode.Compress) CheckResult((Result)codec.SetDictionary(dictionary));
			else this.dictionary = dictionary; // when decompressing, the dictionary will be provided with the first chunk
		}

		/// <summary>Transforms a region of memory.</summary>
		/// <param name="input">The memory to compress or decompress</param>
		/// <param name="flush">The flush mode to use. The default is <see cref="ZlibFlush.Finish"/>, which means that
		/// <paramref name="input"/> represents the entire remaining data to transform.
		/// </param>
#if !NETSTANDARD2_0 && !NET45 && !NET46
		public Memory<byte> Transform(byte[] input, ZlibFlush flush = ZlibFlush.Finish) => Transform(input, 0, input.Length, flush);
#else
		public byte[] Transform(byte[] input, ZlibFlush flush = ZlibFlush.Finish) => Transform(input, 0, input.Length, flush);
#endif

		/// <summary>Transforms a region of memory.</summary>
		/// <param name="input">The array containing the data to compress or decompress</param>
		/// <param name="index">The index within <paramref name="input"/> where the data begins</param>
		/// <param name="length">The number of bytes to transform</param>
		/// <param name="flush">The flush mode to use. The default is <see cref="ZlibFlush.Finish"/>, which means that
		/// <paramref name="input"/> represents the entire remaining data to transform.
		/// </param>
		/// <remarks>It is not efficient to use this method to transform a large amount of data.</remarks>
#if !NETSTANDARD2_0 && !NET45 && !NET46
		public unsafe Memory<byte> Transform(byte[] input, int index, int length, ZlibFlush flush = ZlibFlush.Finish)
#else
		public unsafe byte[] Transform(byte[] input, int index, int length, ZlibFlush flush = ZlibFlush.Finish)
#endif
		{
			Utility.ValidateRange(input, index, length);
			byte[] buffer = new byte[Math.Max(64, Mode == CompressionMode.Compress ? input.Length/4 : input.Length*3/2)];
			int outputLength = 0;
			while(true)
			{
				bool more = Transform(input, index, length, out int read,
					buffer, outputLength, buffer.Length-outputLength, out int written, flush);
				outputLength += written;
				index += read;
				length -= read;
				if(!more) // if we transformed everything (when flush is Finish)
				{
					// if we were decompressing and reached the end of compressed data before the input was exhausted, complain
					if(length != 0) throw new InvalidDataException("Too much data was provided.");
					break; // otherwise, we're done
				}
				else if(outputLength == buffer.Length) // if the buffer is full, maybe we need to provide more output space
				{
					Array.Resize(ref buffer, buffer.Length + Math.Max(64, Mode == CompressionMode.Compress ? length : length*3/2));
				}
				else if(length == 0) // otherwise, we've given all we got, but zlib still wants more
				{
					if(flush == ZlibFlush.Finish) throw new EndOfStreamException(); // if they didn't provide enough data, throw
					break; // otherwise, the user should call us again with more input
				}
			}

#if !NETSTANDARD2_0 && !NET45 && !NET46
			return new Memory<byte>(buffer, 0, outputLength);
#else
			Array.Resize(ref buffer, outputLength);
			return buffer;
#endif
		}

		/// <summary>Transforms a stream.</summary>
		/// <param name="input">The input <see cref="Stream"/> containing the data to compress or decompress</param>
		/// <param name="output">The output <see cref="Stream"/> where the results should be written</param>
		/// <param name="flush">The flush mode to use. The default is <see cref="ZlibFlush.Finish"/>, which means that
		/// <paramref name="input"/> represents the entire remaining data to transform.
		/// </param>
		public unsafe void Transform(Stream input, Stream output, ZlibFlush flush = ZlibFlush.Finish)
		{
			if(input == null) throw new ArgumentNullException(nameof(input));
			if(output == null) throw new ArgumentNullException(nameof(output));
			AssertUsable();
			byte[] inbuf = ArrayPool<byte>.Shared.Rent(4096), outbuf = ArrayPool<byte>.Shared.Rent(4096);
			bool more;
			while(true)
			{
				int read = input.Read(inbuf, 0, inbuf.Length); // read some data from the input
				if(read == 0) // if there was no more data...
				{
					if(Mode == CompressionMode.Decompress && flush == ZlibFlush.Finish) throw new EndOfStreamException();
					break;
				}
				for(int index = 0; index < read; ) // transform the input we read
				{
					more = Transform(inbuf, index, read-index, out int consumed,
						outbuf, 0, outbuf.Length, out int produced, ZlibFlush.None); // transform some of the input
					output.Write(outbuf, 0, produced);
					index += consumed;
					if(!more) // if no more data is needed (e.g. because we reached the end of the decompressed data)
					{
						if(input.CanSeek) input.Seek(index-read, SeekOrigin.Current); // try to put back the extra input we read
						return; // and we're done
					}
				}
			}
			while(true) // at this point, we've transformed all of the input, but we need to flush the remaining output
			{
				more = Transform(inbuf, 0, 0, out int _, outbuf, 0, outbuf.Length, out int produced, flush);
				output.Write(outbuf, 0, produced);
				if(!more || produced == 0) break; // if we've reached the end while decompressing or if no data was produced, we're done
			}
		}

		/// <summary>Transforms a single chunks of data.</summary>
		/// <param name="input">The array containing the data to compress or decompress</param>
		/// <param name="inputIndex">The index within <paramref name="input"/> where the data to transform begins</param>
		/// <param name="inputLength">The number of bytes within <paramref name="input"/> to transform</param>
		/// <param name="bytesRead">A variable that will receive the number of bytes read from the input</param>
		/// <param name="output">The array where the compressed or decompressed data should be written</param>
		/// <param name="outputIndex">The index within <paramref name="output"/> where the transformed data should be written</param>
		/// <param name="outputLength">The available space within <paramref name="output"/> for transformed data to be written</param>
		/// <param name="bytesWritten">A variable that will receive the number of bytes written to the output</param>
		/// <param name="flush">The <see cref="ZlibFlush"/> mode of the transformation</param>
		/// <returns>Returns false if the transformation is complete (when <paramref name="flush"/> is <see cref="ZlibFlush.Finish"/>),
		/// and true otherwise.
		/// </returns>
		public unsafe bool Transform(byte[] input, int inputIndex, int inputLength, out int bytesRead,
			byte[] output, int outputIndex, int outputLength, out int bytesWritten, ZlibFlush flush)
		{
			Utility.ValidateRange(input, inputIndex, inputLength);
			Utility.ValidateRange(output, outputIndex, outputLength);
			AssertUsable();
			used = true;
			codec.InputBuffer = input;
			codec.NextIn = inputIndex;
			codec.AvailableBytesIn = inputLength;
			codec.OutputBuffer = output;
			codec.NextOut = outputIndex;
			codec.AvailableBytesOut = outputLength;
			retry:
			var res = (Result)(Mode == CompressionMode.Compress ?
				codec.Deflate((Ionic.Zlib.FlushType)flush) : codec.Inflate((Ionic.Zlib.FlushType)flush));
			if(res == Result.NeedDict) // if we're decompressing and need the initial dictionary...
			{
				CheckResult((Result)codec.SetDictionary(dictionary)); // provide it
				if(codec.AvailableBytesIn != 0 && codec.AvailableBytesOut != 0) goto retry; // if more space is available, continue
			}
			bytesRead    = codec.NextIn  - inputIndex;
			bytesWritten = codec.NextOut - outputIndex;
			return res != Result.EOF;
		}

		readonly Ionic.Zlib.ZlibCodec codec = new Ionic.Zlib.ZlibCodec();
		byte[] dictionary;
		bool tainted, used;

		// the Ionic.Zlib library just returns integers. these are their meanings...
		enum Result { OK = 0, EOF = 1, NeedDict = 2, Errno = -1, StreamErr = -2, DataErr = -3, MemErr = -4, BufErr = -5, VersionErr = -6 }

		void AssertUsable()
		{
			if(tainted) throw new InvalidOperationException("The codec is tainted due to an error. Call Reset to reset it.");
		}

		void CheckResult(Result result)
		{
			if((int)result < 0) // if an error occurred...
			{
				if(result != Result.BufErr) // a buffer error is not so harmful. it just means we need to give more buffer space
				{
					tainted = true;
					string msg = $"A {result.ToString()} error occurred.";
					if(!string.IsNullOrEmpty(codec.Message)) msg = msg + " " + codec.Message;
					throw new ZlibException(msg);
				}
			}
 			else if(result == Result.NeedDict && dictionary == null) // if we need a dictionary but don't have it...
			{
				tainted = true;
				if(dictionary == null) throw new InvalidOperationException("A dictionary is needed but none was set.");
			}
		}

		static void CheckInitError(Result result)
		{
			if((int)result < 0) throw new ZlibException($"A {result.ToString()} error occurred.");
		}
	}
	#endregion

	#region ZlibException
	/// <summary>Represents an exception related to zlib.</summary>
	public class ZlibException : Exception
	{
		/// <summary>Initializes a new <see cref="ZlibException"/> based on an exception message.</summary>
		public ZlibException(string message) : base(message) { }

		/// <summary>Initializes a new <see cref="ZlibException"/> based on an exception message and inner exception.</summary>
		public ZlibException(string message, Exception innerException) : base(message, innerException) { }
	}
	#endregion

	#region ZlibFlush
	/// <summary>Determines how zlib should finish the transformation of the data.</summary>
	enum ZlibFlush
	{
		/// <summary>No data will be flushed. Some data may remain in the internal zlib buffer.</summary>
		None = 0,
		/// <summary>The data will be flushed and padded just enough to align the output on a byte boundary so it can be output.</summary>
		Sync = 2,
		/// <summary>The data will be completely flushed, allowing compression or decompression to restart at that point in the output
		/// stream.
		/// </summary>
		Full = 3,
		/// <summary>When compressing, the output will be flushed along with an end-of-stream marker that will cause decompression to
		/// stop there. When decompressing, this flush mode isn't necessary, since decompression will always stop at the end-of-stream
		/// marker, but providing it can improve performance slightly and will help detect failures such as truncated data.
		/// </summary>
		Finish = 4
	}
	#endregion
}
