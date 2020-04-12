using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SPDY
{
	#region Pipe
	/// <summary>Represents a pipe that blocks on reads when empty.</summary>
	sealed class Pipe : IDisposable
	{
		/// <summary>Gets the number of bytes available to be read. If not zero, the read methods will not block.</summary>
		public int DataAvailable => buffer.DataAvailable;

		/// <summary>Removes all data from the pipe.</summary>
		public void Clear()
		{
			lock(buffer) buffer.Clear();
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			if(!disposed)
			{
				disposed = true;
				lock(buffer) buffer.Dispose();
				readEvent.Set();
				readEvent.Dispose();
			}
		}

		/// <summary>Called when there will be no more writes to the pipe, this method will unblock waiting readers.</summary>
		public void Finish()
		{
			if(!finished)
			{
				finished = true;
				readEvent.Set();
			}
		}

		/// <summary>Reads some data and returns the number of bytes read. The method will block until data is available or
		/// <see cref="Finish"/> is called.
		/// </summary>
		public int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

		/// <summary>Reads some data and returns the number of bytes read. The method will block until data is available or
		/// <see cref="Finish"/> is called.
		/// </summary>
		public int Read(Span<byte> buffer)
		{
			if(buffer.Length == 0) return 0;
			while(!disposed)
			{
				int read;
				lock(this.buffer) read = this.buffer.Read(buffer);
				if(read != 0 || finished) return read;
				readEvent.Wait();
			}
			throw new ObjectDisposedException(GetType().FullName);
		}

		/// <summary>Reads some data and returns the number of bytes read. The task will wait until data is available,
		/// <see cref="Finish"/> is called, or the task is canceled.
		/// </summary>
#if !NETSTANDARD2_0 && !NET45 && !NET46
		public ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancelToken = default) =>
#else
		public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancelToken = default) =>
#endif
			ReadAsync(new Memory<byte>(buffer, offset, count), cancelToken);

		/// <summary>Reads some data and returns the number of bytes read. The task will wait until data is available,
		/// <see cref="Finish"/> is called, or the task is canceled.
		/// </summary>
#if !NETSTANDARD2_0 && !NET45 && !NET46
		public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancelToken = default)
#else
		public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancelToken = default)
#endif
		{
			if(buffer.Length == 0) return 0;
			while(!disposed)
			{
				cancelToken.ThrowIfCancellationRequested();
				int read;
				lock(this.buffer) read = this.buffer.Read(buffer.Span);
				if(read != 0 || finished) return read;
				await readEvent.WaitAsync(cancelToken).ConfigureAwait(false);
			}
			throw new ObjectDisposedException(GetType().FullName);
		}

		/// <summary>Writes data into the pipe. This method will not block.</summary>
		public void Write(byte[] data, int offset, int count) => Write(new ReadOnlySpan<byte>(data, offset, count));

		/// <summary>Writes data into the pipe. This method will not block.</summary>
		public void Write(ReadOnlySpan<byte> data)
		{
			if(disposed) throw new ObjectDisposedException(GetType().FullName);
			if(finished) throw new InvalidOperationException("The pipe is draining.");
			if(data.Length != 0)
			{
				lock(buffer) buffer.Write(data);
				readEvent.Set();
			}
		}

		readonly StreamBuffer buffer = new StreamBuffer();
		readonly AsyncAutoResetEvent readEvent = new AsyncAutoResetEvent();
		bool disposed, finished;
	}
#endregion

	#region StreamBuffer
	/// <summary>Implements a buffer into which data can be written and from which data can be read.</summary>
	sealed class StreamBuffer : IDisposable
	{
		/// <summary>Gets the number of bytes available in the buffer.</summary>
		public int DataAvailable { get; private set; }

		/// <summary>Removes all data from the buffer.</summary>
		public void Clear()
		{
			foreach(Chunk chunk in dataList) chunk.Dispose();
			dataList.Clear();
			DataAvailable = 0;
		}

		/// <inheritdoc/>
		public void Dispose() => Clear();

		/// <summary>Reads data from the buffer and returns the number of bytes read. This method will not block.</summary>
		public int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

		/// <summary>Reads data from the buffer and returns the number of bytes read. This method will not block.</summary>
		public int Read(Span<byte> buffer)
		{
			int read = 0;
			while(buffer.Length != 0) // while there's space left in the buffer...
			{
				Chunk chunk = dataList.First?.Value; // get the first data chunk in the list
				if(chunk == null || chunk.AvailableData == 0) break; // if it's nonexistent or empty, we're done
				int toCopy = Math.Min(chunk.AvailableData, buffer.Length); // otherwise, see how much we should get out of it
				chunk.Read(toCopy, buffer); // read the data into the buffer
				if(chunk.AvailableData == 0 && chunk.AvailableSpace == 0) // if the chunk became empty...
				{
					if(chunk.Buffer.Length > DropThreshold || dataList.Count > 1) // if the chunk is extra large or we have others...
					{
						dataList.RemoveFirst(); // remove this one
						chunk.Dispose();
					}
					else // otherwise, reset it. (we'll keep one normal-sized chunk around so we don't have to create a new one)
					{
						chunk.Reset();
					}
				}
				read += toCopy;
				buffer = buffer.Slice(toCopy);
			}
			DataAvailable -= read;
			return read;
		}

		/// <summary>Writes data into the buffer. This method will not block.</summary>
		public void Write(byte[] data, int offset, int count) => Write(new ReadOnlySpan<byte>(data, offset, count));

		/// <summary>Writes data into the buffer. This method will not block.</summary>
		public void Write(ReadOnlySpan<byte> data)
		{
			int length = data.Length;
			if(length != 0)
			{
				if(dataList.Count == 0) dataList.AddLast(new Chunk(length)); // if the chunk list is empty, add a chunk
				do // while there's still data left to write...
				{
					Chunk chunk = dataList.Last.Value; // get the last chunk
					if(chunk.AvailableSpace == 0) // if it's full...
					{
						chunk = new Chunk(data.Length); // add a new chunk
						dataList.AddLast(chunk);
					}
					int toCopy = Math.Min(chunk.AvailableSpace, data.Length); // now copy as much as we can into the chunk
					chunk.Write(data.Slice(0, toCopy));
					data = data.Slice(toCopy);
				} while(data.Length != 0);
				DataAvailable += length;
			}
		}

		const int MinChunkSize = 32*1024, DropThreshold = 64*1024;

		#region Chunk
		/// <summary>Represents a chunk of data. We maintain multiple chunks of data rather than a single large buffer to avoid having
		/// to reallocate arrays or copy data.
		/// </summary>
		sealed class Chunk
		{
			public Chunk(int minSize) => Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(MinChunkSize, minSize));
			public int AvailableData => WriteIndex - ReadIndex;
			public int AvailableSpace => Buffer.Length - WriteIndex;
			public int ReadIndex { get; private set; }
			public int WriteIndex { get; private set; }

			public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);

			public void Reset() => ReadIndex = WriteIndex = 0;

			public void Read(int byteCount, Span<byte> dest)
			{
				new Span<byte>(Buffer, ReadIndex, byteCount).CopyTo(dest);
				ReadIndex += byteCount;
			}

			public void Write(ReadOnlySpan<byte> span)
			{
				span.CopyTo(new Span<byte>(Buffer, WriteIndex, AvailableSpace));
				WriteIndex += span.Length;
			}

			public readonly byte[] Buffer;
		}
		#endregion

		readonly LinkedList<Chunk> dataList = new LinkedList<Chunk>();
	}
	#endregion
}
