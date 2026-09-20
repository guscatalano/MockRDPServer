using System.Buffers.Binary;

namespace MockRdp.Rdp;

public enum InputEventType { Scancode, Mouse, MouseX, Sync, Unicode, Qoe, Unknown }

/// <summary>A decoded client input event (mouse position in X/Y, key in Code).</summary>
public readonly record struct InputEvent(InputEventType Type, ushort Flags, ushort X, ushort Y, byte Code);

/// <summary>
/// Fast-path client input (MS-RDPBCGR 2.2.8.1.2). The fast-path header byte carries the event
/// count in bits 2–3; each event has a 3-bit event code in the high bits of its header byte.
/// </summary>
public static class Input
{
    // pointerFlags bits (mouse events).
    public const ushort PtrFlagsMove = 0x0800;
    public const ushort PtrFlagsDown = 0x8000;
    public const ushort PtrFlagsButton1 = 0x1000;

    // Slow-path keyboard flags (TS_KEYBOARD_EVENT), distinct from fast-path's bit flags.
    private const ushort KbdFlagsRelease = 0x8000;
    private const ushort KbdFlagsExtended = 0x0100;

    /// <summary>
    /// Slow-path client input (TS_INPUT_PDU_DATA, MS-RDPBCGR 2.2.8.1.1.3), which mstsc/mstscax
    /// send over the I/O channel. Normalises each event to the same <see cref="InputEvent"/>
    /// convention as <see cref="ParseFastPath"/> (scancode release = flags bit 0). The argument
    /// is the Share Data PDU; input data begins after the 18-byte header.
    /// </summary>
    public static List<InputEvent> ParseSlowPath(ReadOnlySpan<byte> shareDataPdu)
    {
        var events = new List<InputEvent>();
        if (shareDataPdu.Length < 22) return events;

        int pos = 18;
        int num = BinaryPrimitives.ReadUInt16LittleEndian(shareDataPdu.Slice(pos, 2));
        pos += 4; // numberEvents (2) + pad (2)

        for (int i = 0; i < num && pos + 12 <= shareDataPdu.Length; i++)
        {
            pos += 4; // eventTime
            ushort messageType = BinaryPrimitives.ReadUInt16LittleEndian(shareDataPdu.Slice(pos, 2));
            ushort f = BinaryPrimitives.ReadUInt16LittleEndian(shareDataPdu.Slice(pos + 2, 2));
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(shareDataPdu.Slice(pos + 4, 2));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(shareDataPdu.Slice(pos + 6, 2));
            pos += 8; // messageType (2) + slowPathInputData (6)

            switch (messageType)
            {
                case 0x0004: // INPUT_EVENT_SCANCODE: keyboardFlags(f), keyCode(a)
                    ushort flags = (ushort)(((f & KbdFlagsRelease) != 0 ? 0x01 : 0x00) | ((f & KbdFlagsExtended) != 0 ? 0x02 : 0x00));
                    events.Add(new InputEvent(InputEventType.Scancode, flags, 0, 0, (byte)a));
                    break;
                case 0x8001: // INPUT_EVENT_MOUSE: pointerFlags(f), x(a), y(b)
                    events.Add(new InputEvent(InputEventType.Mouse, f, a, b, 0));
                    break;
                case 0x8002: // INPUT_EVENT_MOUSEX
                    events.Add(new InputEvent(InputEventType.MouseX, f, a, b, 0));
                    break;
                case 0x0005: // INPUT_EVENT_UNICODE: unicodeCode(a)
                    events.Add(new InputEvent(InputEventType.Unicode, f, a, 0, 0));
                    break;
            }
        }
        return events;
    }

    public static List<InputEvent> ParseFastPath(byte fastPathHeader, ReadOnlySpan<byte> payload)
    {
        var events = new List<InputEvent>();
        int numEvents = (fastPathHeader >> 2) & 0x0F;
        int pos = 0;
        if (numEvents == 0)
        {
            if (payload.Length == 0) return events;
            numEvents = payload[pos++];
        }

        for (int i = 0; i < numEvents && pos < payload.Length; i++)
        {
            byte header = payload[pos++];
            int code = (header >> 5) & 0x07;
            var flags = (ushort)(header & 0x1F);

            switch (code)
            {
                case 0: // FASTPATH_INPUT_EVENT_SCANCODE
                    if (pos >= payload.Length) return events;
                    events.Add(new InputEvent(InputEventType.Scancode, flags, 0, 0, payload[pos++]));
                    break;

                case 1: // FASTPATH_INPUT_EVENT_MOUSE
                case 2: // FASTPATH_INPUT_EVENT_MOUSEX
                    if (pos + 6 > payload.Length) return events;
                    ushort pointerFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos, 2));
                    ushort x = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos + 2, 2));
                    ushort y = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos + 4, 2));
                    pos += 6;
                    events.Add(new InputEvent(code == 1 ? InputEventType.Mouse : InputEventType.MouseX, pointerFlags, x, y, 0));
                    break;

                case 3: // FASTPATH_INPUT_EVENT_SYNC
                    events.Add(new InputEvent(InputEventType.Sync, flags, 0, 0, 0));
                    break;

                case 4: // FASTPATH_INPUT_EVENT_UNICODE
                    if (pos + 2 > payload.Length) return events;
                    ushort unicode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos, 2));
                    pos += 2;
                    events.Add(new InputEvent(InputEventType.Unicode, flags, unicode, 0, 0));
                    break;

                case 6: // FASTPATH_INPUT_EVENT_QOE_TIMESTAMP
                    if (pos + 4 > payload.Length) return events;
                    pos += 4;
                    events.Add(new InputEvent(InputEventType.Qoe, 0, 0, 0, 0));
                    break;

                default:
                    return events; // unknown event code — cannot safely continue
            }
        }
        return events;
    }
}
