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
