using System.IO.Ports;
using Microsoft.Win32;

namespace ScreenUpdater.Lcd;

/// <summary>Turing Smart Screen 3.5" (revision A) serial protocol, driven in landscape.</summary>
public sealed class TuringLcd : IDisposable
{
    public const int Width = 480;
    public const int Height = 320;

    const int Landscape = 2;
    const int ReverseLandscape = 3;

    const byte CmdClear = 102;
    const byte CmdScreenOff = 108;
    const byte CmdScreenOn = 109;
    const byte CmdSetBrightness = 110;
    const byte CmdSetOrientation = 121;
    const byte CmdDisplayBitmap = 197;
    const byte CmdHello = 69;

    readonly SerialPort _port;

    /// <param name="flip">Rotate the image by 180°.</param>
    public TuringLcd(string portName, bool flip)
    {
        _port = new SerialPort(portName, 115200)
        {
            Handshake = Handshake.RequestToSend,
            ReadTimeout = 1000,
            WriteTimeout = 5000,
            WriteBufferSize = 1 << 20,
        };
        _port.Open();

        Hello();
        SetOrientation(flip ? ReverseLandscape : Landscape);
    }

    /// <summary>COM port of the screen, looked up by its USB identity.</summary>
    public static string? FindPort()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_5722\USB35INCHIPSV2\Device Parameters");
        return key?.GetValue("PortName") as string;
    }

    void Hello()
    {
        _port.DiscardInBuffer();
        _port.Write([CmdHello, CmdHello, CmdHello, CmdHello, CmdHello, CmdHello], 0, 6);
        var reply = new byte[6];
        try
        {
            int read = 0;
            while (read < reply.Length)
                read += _port.Read(reply, read, reply.Length - read);
        }
        catch (TimeoutException)
        {
            // Original Turing firmware does not answer HELLO.
        }
        _port.DiscardInBuffer();
    }

    static byte[] Header(int x, int y, int ex, int ey, byte cmd) =>
    [
        (byte)(x >> 2),
        (byte)(((x & 3) << 6) + (y >> 4)),
        (byte)(((y & 15) << 4) + (ex >> 6)),
        (byte)(((ex & 63) << 2) + (ey >> 8)),
        (byte)(ey & 255),
        cmd,
    ];

    void Send(byte cmd, int x = 0, int y = 0, int ex = 0, int ey = 0) =>
        _port.Write(Header(x, y, ex, ey, cmd), 0, 6);

    void SetOrientation(int orientation)
    {
        var buf = new byte[16];
        buf[5] = CmdSetOrientation;
        buf[6] = (byte)(orientation + 100);
        buf[7] = (byte)(Width >> 8);
        buf[8] = (byte)(Width & 255);
        buf[9] = (byte)(Height >> 8);
        buf[10] = (byte)(Height & 255);
        _port.Write(buf, 0, buf.Length);
    }

    /// <param name="percent">0 (darkest) to 100 (brightest).</param>
    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Send(CmdSetBrightness, x: 255 - percent * 255 / 100);
    }

    public void ScreenOff() => Send(CmdScreenOff);

    public void ScreenOn() => Send(CmdScreenOn);

    public void Clear() => Send(CmdClear);

    /// <summary>Writes a rectangle of RGB565 pixels taken from a full-frame buffer.</summary>
    public void DrawRegion(ushort[] frame, int x, int y, int w, int h)
    {
        Send(CmdDisplayBitmap, x, y, x + w - 1, y + h - 1);

        var bytes = new byte[w * h * 2];
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(frame, ((y + row) * Width + x) * 2, bytes, row * w * 2, w * 2);
        }

        int chunk = w * 8;
        for (int offset = 0; offset < bytes.Length; offset += chunk)
        {
            _port.Write(bytes, offset, Math.Min(chunk, bytes.Length - offset));
        }
    }

    public void Dispose() => _port.Dispose();
}
