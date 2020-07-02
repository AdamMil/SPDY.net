using System;
using System.Buffers;

namespace SPDY
{
	#region SPDYFrameFlag
	/// <summary>The flag values within the flag byte in the SPDY frame header.</summary>
	[Flags]
	public enum SPDYFrameFlags
	{
		/// <summary>The frame has no flags.</summary>
		None = 0,
		/// <summary>This frame is the last frame that will be sent for a given stream.</summary>
		Final = 1,
		/// <summary>The stream being created must not be written to by the other side. This only applies to
		/// <see cref="SPDYFrameType.Stream"/> frames.
		/// </summary>
		Unidirectional = 2,
		/// <summary>Existing setting values should be cleared. This only applies to <see cref="SPDYFrameType.Settings"/> frames.</summary>
		Clear = 1
	}
	#endregion

	#region SPDYFrameType
	/// <summary>Describes the type of a SPDY control frame.</summary>
	public enum SPDYFrameType
	{
		/// <summary>The type of a frame with <see cref="SPDYFrame.IsValid"/> set to false.</summary>
		Invalid = 0,
		/// <summary>The SYN_STREAM frame is a request to open a new stream.</summary>
		Stream = 1,
		/// <summary>The SYN_REPLY frame indicates acceptance of a new stream.</summary>
		Reply = 2,
		/// <summary>The RST_STREAM from indicates closure of an existing stream or rejection of a new stream.</summary>
		Reset = 3,
		/// <summary>The SETTINGS frame is used to communicate protocol-related settings to the other side.</summary>
		Settings = 4,
		/// <summary>The PING frame is used to measure latency.</summary>
		Ping = 6,
		/// <summary>The GOAWAY frame is used to indicate that the other side must not create any more streams, which is often
		/// interpreted to mean that the connection is about to be closed.
		/// </summary>
		GoAway = 7,
		/// <summary>The HEADERS frame adds additional headers to a stream.</summary>
		Headers = 8,
		/// <summary>The WINDOW_UPDATE frame adjusts the flow control window for a stream or the connection.</summary>
		Window = 9,
	}
	#endregion

	#region SPDYFrame
	/// <summary>Represents a SPDY frame.</summary>
	public struct SPDYFrame : IDisposable
	{
		/// <summary>Initializes a new data frame of the given length. The <see cref="Data"/> array may be filled with garbage.</summary>
		public SPDYFrame(int streamId, SPDYFrameFlags flags, int length)
		{
			if((uint)length > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(length));
			_data = ArrayPool<byte>.Shared.Rent(length);
			_streamId = (uint)streamId;
			_flagsAndLength = ((uint)flags<<24) | (uint)length;
		}

		/// <summary>Initializes a new control frame of the given type. The <see cref="Data"/> array may be filled with garbage.</summary>
		public SPDYFrame(SPDYFrameType type, SPDYFrameFlags flags, int length)
		{
			if((uint)length > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(length));
			_data = ArrayPool<byte>.Shared.Rent(length);
			_streamId = (uint)type | 0x80030000;
			_flagsAndLength = ((uint)flags<<24) | (uint)length;
		}

		/// <summary>Initializes a new <see cref="SPDYFrame"/> by reading the header from a data array.</summary>
		public SPDYFrame(byte[] buffer, int index)
		{
			_streamId = ReadUint(buffer, index);
			uint flagsAndLength = ReadUint(buffer, index+4);
			_flagsAndLength = flagsAndLength;
			_data = ArrayPool<byte>.Shared.Rent((int)flagsAndLength & 0xFFFFFF);
		}

		/// <summary>Indicates whether the frame is a control frame. If false, the frame is a data frame.</summary>
		public bool IsControlFrame => (int)_streamId < 0;

		/// <summary>Indicates whether the frame has the <see cref="SPDYFrameFlags.Final"/> flag. This property may give incorrect
		/// results if used on frames for which the <see cref="SPDYFrameFlags.Final"/> flag is not legal.
		/// </summary>
		public bool IsFinal => (Flags & SPDYFrameFlags.Final) != 0;

		/// <summary>Gets the data block for the frame.</summary>
		public byte[] Data => _data;

		/// <summary>Gets the number of bytes that are or can be stored in the data block.</summary>
		public int DataLength => (int)(_flagsAndLength & 0xFFFFFF);

		/// <summary>Gets the set of <see cref="SPDYFrameFlags"/> for the frame.</summary>
		public SPDYFrameFlags Flags => (SPDYFrameFlags)(_flagsAndLength >> 24);

		/// <summary>Indicates whether this is a valid frame. If false, none of the other properties should be used.</summary>
		public bool IsValid => _data != null;

		/// <summary>Gets a <see cref="Span{T}"/> of byte referencing the data block of the frame.</summary>
		public Span<byte> Span => new Span<byte>(Data, 0, DataLength);

		/// <summary>Gets the ID of the stream that the frame refers to. This property is only valid for data frames.</summary>
		public int StreamId => (int)_streamId;

		/// <summary>Gets the type of control frame. This property is only valid for control frames.</summary>
		public SPDYFrameType Type => (SPDYFrameType)(ushort)_streamId;

		/// <summary>Gets the version of the SPDY protocol used to generate the frame.</summary>
		public int Version => (int)((_streamId>>16) & 0x7FFF);

		/// <summary>Returns the <see cref="Data"/> array to the shared buffer pool.</summary>
		public void Dispose()
		{
			if(_data != null)
			{
				ArrayPool<byte>.Shared.Return(_data);
				_data = null;
			}
		}

		/// <summary>Indicates whether the frame has all the given flags.</summary>
		public bool HasFlags(SPDYFrameFlags flags) => (Flags & flags) == flags;

		/// <summary>Reads a signed integer from the given index within the frame's data block.</summary>
		public int ReadInt(int index) => (int)ReadUint(index);

		/// <summary>Reads an unsigned integer from the given index within the frame's data block.</summary>
		public uint ReadUint(int index)
		{
			if((uint)index > (uint)DataLength-4) throw new ArgumentOutOfRangeException();
			return ReadUint(_data, index);
		}

		/// <inheritdoc/>
		public override string ToString()
		{
			if(!IsControlFrame)
			{
				return $"Data ({DataLength.ToString()} bytes{((Flags & SPDYFrameFlags.Final) != 0 ? ", final" : null)} " +
					$"for stream {StreamId.ToString()})";
			}

			string str = $"{Type.ToString()} (flags {(int)Flags})", suffix = null;
			if(IsControlFrame)
			{
				switch(Type)
				{
					case SPDYFrameType.GoAway: suffix = ((SPDYShutdownReason)ReadInt(4)).ToString(); break;
					case SPDYFrameType.Ping: suffix = ReadUint(0).ToString(); break;
					case SPDYFrameType.Reply: suffix = "stream " + ReadInt(0).ToString(); break;
					case SPDYFrameType.Reset:
						suffix = $"({((SPDYResetReason)ReadInt(4)).ToString()}) for stream {ReadInt(0).ToString()}";
						break;
					case SPDYFrameType.Stream:
						suffix = $"stream {ReadInt(0).ToString()} associated with {ReadInt(4).ToString()}";
						break;
					case SPDYFrameType.Window:
						suffix = $"+{ReadInt(4).ToString()} bytes for stream {ReadInt(0).ToString()}";
						break;
				}
			}
			return suffix == null ? str : str + " " + suffix;
		}

		/// <summary>Writes a signed integer to the given index within the frame's data block.</summary>
		public void Write(int value, int index) => Write((uint)value, index);

		/// <summary>Writes an unsigned integer to the given index within the frame's data block.</summary>
		public void Write(uint value, int index)
		{
			if((uint)index > (uint)DataLength-4) throw new ArgumentOutOfRangeException();
			Write(_data, index, value);
		}

		/// <summary>Creates a new data frame.</summary>
		/// <param name="streamId">The ID of the stream</param>
		/// <param name="data">The data that should be sent</param>
		/// <param name="final">Whether this is the last frame referring to the given stream</param>
		public static SPDYFrame DataFrame(int streamId, ReadOnlySpan<byte> data, bool final)
		{
			if(streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
			var f = new SPDYFrame(streamId, final ? SPDYFrameFlags.Final : 0, data.Length);
			data.CopyTo(new Span<byte>(f.Data));
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.GoAway"/> frame.</summary>
		/// <param name="lastAcceptedStream">The ID of the last stream accepted from the other side, or zero if no streams were accepted.</param>
		/// <param name="reason">The <see cref="SPDYShutdownReason"/> for the shutdown</param>
		public static SPDYFrame GoAway(int lastAcceptedStream, SPDYShutdownReason reason)
		{
			if(lastAcceptedStream < 0) throw new ArgumentOutOfRangeException(nameof(lastAcceptedStream));
			var f = new SPDYFrame(SPDYFrameType.GoAway, 0, 8);
			f.Write(lastAcceptedStream, 0);
			f.Write((uint)reason, 4);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Headers"/> frame.</summary>
		/// <param name="streamId">The ID of the stream whose headers should be updated</param>
		/// <param name="headerData">The additional header data as returned from <see cref="SPDYConnection.CompressHeaders"/></param>
		/// <param name="final">Whether this is the last frame referring to the given stream. The default is false.</param>
		public static SPDYFrame Headers(int streamId, byte[] headerData, bool final = false)
		{
			if(streamId <= 0) throw new ArgumentOutOfRangeException();
			var f = new SPDYFrame(SPDYFrameType.Headers, final ? SPDYFrameFlags.Final : 0, headerData.Length+4);
			f.Write(streamId, 0);
			Array.Copy(headerData, 0, f.Data, 4, headerData.Length);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Stream"/> frame.</summary>
		/// <param name="streamId">The ID of the new stream</param>
		/// <param name="headerData">The initial header data as returned from <see cref="SPDYConnection.CompressHeaders"/></param>
		/// <param name="unidirectional">If true, the stream must not be written to by the other side (i.e. is write-only). The default
		/// is false.
		/// </param>
		/// <param name="final">If true, the stream must not be written to by this side (i.e. is read-only). The default is false.</param>
		/// <param name="associatedStreamId">The ID of a stream that the new stream is associated with, or 0 if it's not associated with
		/// any other streams. The default is 0.
		/// </param>
		/// <param name="priority">The priority of the stream, from 0 (highest priority) to 7 (lowest priority). The default is 3.</param>
		public static SPDYFrame NewStream(int streamId, byte[] headerData, bool unidirectional = false, bool final = false, int associatedStreamId = 0,
		                                  int priority = 3)
		{
			if(streamId <= 0 || associatedStreamId < 0 || (uint)priority > 7) throw new ArgumentOutOfRangeException();
			var flags = (unidirectional ? SPDYFrameFlags.Unidirectional : 0) | (final ? SPDYFrameFlags.Final : 0);
			var f = new SPDYFrame(SPDYFrameType.Stream, flags, headerData.Length+10);
			f.Write(streamId, 0);
			f.Write(associatedStreamId, 4);
			f.Data[8] = (byte)(priority << 5);
			f.Data[9] = 0;
			Array.Copy(headerData, 0, f.Data, 10, headerData.Length);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Ping"/> frame.</summary>
		/// <param name="id">The ID of the ping frame</param>
		public static SPDYFrame Ping(uint id)
		{
			var f = new SPDYFrame(SPDYFrameType.Ping, 0, 4);
			f.Write(id, 0);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Settings"/> frame.</summary>
		/// <param name="clearPreviousSettings">If true, the recipient should clear previously persisted settings</param>
		/// <param name="settings">An array of <see cref="SPDYSetting"/> objects describing the new setting values</param>
		public static SPDYFrame Settings(bool clearPreviousSettings, params SPDYSetting[] settings)
		{
			if(settings == null) throw new ArgumentNullException();
			var f = new SPDYFrame(
				SPDYFrameType.Settings, clearPreviousSettings ? SPDYFrameFlags.Clear : 0, settings != null ? settings.Length*8+4 : 4);
			f.Write(settings.Length, 0);
			for(int index = 4, i = 0; i < settings.Length; index += 8, i++)
			{
				SPDYSetting s = settings[i];
				f.Write(s._flagsAndId, index);
				f.Write(s.Value, index+4);
			}
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Reply"/> frame.</summary>
		/// <param name="streamId">The ID of the stream that is being accepted</param>
		/// <param name="headerData">Additional header data as returned from <see cref="SPDYConnection.CompressHeaders"/></param>
		/// <param name="final">Whether this is the last frame referring to the given stream. The default is false.</param>
		public static SPDYFrame Reply(int streamId, byte[] headerData, bool final = false)
		{
			if(streamId <= 0) throw new ArgumentOutOfRangeException();
			var f = new SPDYFrame(SPDYFrameType.Reply, final ? SPDYFrameFlags.Final : 0, headerData.Length+4);
			f.Write(streamId, 0);
			Array.Copy(headerData, 0, f.Data, 4, headerData.Length);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Reset"/> frame.</summary>
		/// <param name="streamId">The ID of the stream that is being closed</param>
		/// <param name="reason">The <see cref="SPDYResetReason"/> why the stream is being closed</param>
		public static SPDYFrame Reset(int streamId, SPDYResetReason reason)
		{
			if(streamId <= 0) throw new ArgumentOutOfRangeException();
			var f = new SPDYFrame(SPDYFrameType.Reset, 0, 8);
			f.Write(streamId, 0);
			f.Write((uint)reason, 4);
			return f;
		}

		/// <summary>Creates a new <see cref="SPDYFrameType.Window"/> frame.</summary>
		/// <param name="streamId">The ID of the stream whose control flow window is being enlarged, or 0 to enlarge the connection's
		/// global control flow window
		/// </param>
		/// <param name="delta">The number of additional bytes that the recipient is allowed to send</param>
		public static SPDYFrame Window(int streamId, int delta)
		{
			if(streamId < 0 || delta <= 0) throw new ArgumentOutOfRangeException();
			var f = new SPDYFrame(SPDYFrameType.Window, 0, 8);
			f.Write(streamId, 0);
			f.Write(delta, 4);
			return f;
		}

		/// <summary>The size of the frame header, in bytes.</summary>
		internal const int HeaderSize = 8;

		/// <summary>Copies the frame's 8-byte header to the given array.</summary>
		internal void CopyHeaderTo(byte[] data)
		{
			Write(data, 0, _streamId);
			Write(data, 4, _flagsAndLength);
		}

		/// <summary>Reads a signed integer from an array in network (big-endian) order</summary>
		internal static int ReadInt(byte[] data, int index) => (int)ReadUint(data, index);

		/// <summary>Reads an unsigned integer from an array in network (big-endian) order</summary>
		internal static uint ReadUint(byte[] data, int index)
		{
			return ((uint)data[index] << 24) | ((uint)data[index+1] << 16) | ((uint)data[index+2] << 8) | data[index+3];
		}

		/// <summary>Writes a signed integer to an array in network (big-endian) order</summary>
		internal static void Write(byte[] data, int index, int value) => Write(data, index, (uint)value);

		/// <summary>Writes an unsigned integer to an array in network (big-endian) order</summary>
		internal static void Write(byte[] data, int index, uint value)
		{
			data[index  ] = (byte)(value >> 24);
			data[index+1] = (byte)(value >> 16);
			data[index+2] = (byte)(value >> 8);
			data[index+3] = (byte)value;
		}

		readonly uint _streamId, _flagsAndLength;
		byte[] _data;
	}
	#endregion

	#region SPDYResetReason
	/// <summary>Describes the reason why a stream was closed.</summary>
	public enum SPDYResetReason : int
	{
		/// <summary>The stream hasn't been closed.</summary>
		NotReset = 0,
		/// <summary>The SPDY protocol was violated.</summary>
		ProtocolError = 1,
		/// <summary>A <see cref="SPDYFrameType.Reset"/> frame is returned when a frame is received for a stream that is not active.</summary>
		InvalidStream = 2,
		/// <summary>The request to open a new stream was rejected.</summary>
		RefusedStream = 3,
		/// <summary>Issued when the recipient of a stream request does not support the version requested</summary>
		UnsupportedVersion = 4,
		/// <summary>Issued by the creator of a stream to indicate that it's no longer needed</summary>
		Cancel = 5,
		/// <summary>Some part of the SPDY implementation has failed, not due to any protocol violation.</summary>
		InternalError = 6,
		/// <summary>The peer has violated the flow control protocol for the stream.</summary>
		FlowControlError = 7,
		/// <summary>Issued when a <see cref="SPDYFrameType.Reply"/> frame is received for a stream that's already open</summary>
		StreamInUse = 8,
		/// <summary>Issued when a data or <see cref="SPDYFrameType.Reply"/> frame was received for a stream that's half-closed</summary>
		StreamAlreadyClosed = 9,
		/// <summary>Issued when a frame is too large for the implementation to handle</summary>
		FrameTooLarge = 11
	}
	#endregion

	#region SPDYSettingFlags
	/// <summary>Describes the flags for an individual <see cref="SPDYSetting"/>.</summary>
	public enum SPDYSettingFlags : byte
	{
		/// <summary>The setting has no flags.</summary>
		None = 0,
		/// <summary>If set, the recipient should persist the value. This can only be set by the server.</summary>
		Persist = 1,
		/// <summary>If set, the sender is notifying the recipient that the setting was previously persisted and is being returned.</summary>
		Persisted = 2
	}
	#endregion

	#region SPDYSettingId
	/// <summary>Identifies a <see cref="SPDYSetting"/> value.</summary>
	public enum SPDYSettingId : uint
	{
		/// <summary>A nonexisting setting ID.</summary>
		Invalid = 0,
		/// <summary>The sender is estimating its maximum upload bandwidth in KB/s.</summary>
		UploadBandwidth = 1,
		/// <summary>The sender is estimating its maximum download bandwidth in KB/s.</summary>
		DownloadBandwidth = 2,
		/// <summary>The sender is stating its expected latency in milliseconds.</summary>
		RoundTripMs = 3,
		/// <summary>The sender is communicating the maximum number of concurrent streams it will allow. (By default there is no maximum.)</summary>
		MaxStreams = 4,
		/// <summary>The sender is communicating its current TCP congestion window (CWND) value.</summary>
		CurrentCWND = 5,
		/// <summary>The sender is communicating its retransmission ratio.</summary>
		DownloadRetransRate = 6,
		/// <summary>The sender is communicating the initial window size on its end for newly created streams.</summary>
		InitialWindowSize = 7,
		/// <summary>The server is informing the client of the new size of the client certificate vector.</summary>
		ClientCertifateVectorSize = 8
	}
	#endregion

	#region SPDYSetting
	/// <summary>Represents a single setting for the <see cref="SPDYConnection"/>.</summary>
	public struct SPDYSetting
	{
		/// <summary>Initializes a new <see cref="SPDYSetting"/> from its ID, value, and flags.</summary>
		public SPDYSetting(SPDYSettingId id, uint value, SPDYSettingFlags flags = 0)
		{
			_flagsAndId = (uint)id | ((uint)flags << 24);
			Value = value;
		}

		/// <summary>Initializes a new <see cref="SPDYSetting"/> by reading it out of an array.</summary>
		public SPDYSetting(byte[] data, int index)
		{
			_flagsAndId = SPDYFrame.ReadUint(data, 0);
			Value = SPDYFrame.ReadUint(data, 4);
		}

		/// <summary>The <see cref="SPDYSettingFlags"/> of the setting.</summary>
		public SPDYSettingFlags Flags => (SPDYSettingFlags)(_flagsAndId >> 24);

		/// <summary>The <see cref="SPDYSettingId"/> of the setting.</summary>
		public SPDYSettingId ID => (SPDYSettingId)(_flagsAndId & 0xFFFFFF);

		internal readonly uint _flagsAndId;

		/// <summary>The value of the setting.</summary>
		public readonly uint Value;
	}
	#endregion

	#region SPDYShutdownReason
	/// <summary>Gets the reason why a <see cref="SPDYFrameType.GoAway"/> frame was sent.</summary>
	public enum SPDYShutdownReason : int
	{
		/// <summary>The session is being closed normally.</summary>
		Normal = 0,
		/// <summary>The session is being closed because a protocol violation was detected.</summary>
		ProtocolError = 1,
		/// <summary>The session is being closed because an internal error occurred, not related to a protocol violation.</summary>
		InternalError = 2
	}
	#endregion
}
