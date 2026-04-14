namespace PsxAnalyzer.Utils;

internal static class BinaryHelpers
{
    public static void HexDump(byte[] data, int offset, int length)
    {
        const int bytesPerLine = 16;

        for (var i = 0; i < length; i += bytesPerLine)
        {
            var lineOffset = offset + i;
            var lineLength = Math.Min(bytesPerLine, length - i);

            // Offset
            Console.Write($"{lineOffset:X8}  ");

            // Hex bytes
            for (var j = 0; j < bytesPerLine; j++)
            {
                if (j < lineLength)
                    Console.Write($"{data[lineOffset + j]:X2} ");
                else
                    Console.Write("   ");

                if (j == 7) Console.Write(" ");
            }

            Console.Write(" ");

            // ASCII
            for (var j = 0; j < lineLength; j++)
            {
                var b = data[lineOffset + j];
                Console.Write(b is >= 32 and < 127 ? (char)b : '.');
            }

            Console.WriteLine();
        }
    }
}
