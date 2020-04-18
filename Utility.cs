using System;

namespace SPDY
{
	static class Utility
	{
		public static void ValidateRange(Array array, int index, int count)
		{
			if(array == null) throw new ArgumentNullException();
			if((index | count) < 0 || (uint)(index + count) > (uint)array.Length) throw new ArgumentOutOfRangeException();
		}
	}
}
