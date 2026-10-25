using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using MGSC;

namespace QuasimorphCoop.Net
{
    internal enum MsgType : byte
    {
        Hello = 1,      // client -> host: versions
        Reject,         // host -> client: reason
        FileChunk,      // host -> client: save file piece
        FilesDone,      // host -> client: all files sent, load them
        Ready,          // client -> host: dungeon loaded, party spawned
        Go,             // host -> client: gameplay RNG seed, start lockstep
        Batch,          // either: player actions for the sender's merc
        Hash,           // either: state fingerprint after an action point
        Bye,            // either: leaving the session
        Ping,           // either: keepalive
        Welcome,        // host -> client: accepted, connection lasts across levels
        ShipChunk       // either: piece of a compressed ship message (gear between missions, see ShipSync)
    }

    // Actions that bypass PlayerCommandQueue and are called directly from input handling.
    internal enum DirectAction : byte
    {
        SkipTurn = 1,
        MoveStateNext,
        MoveStatePrev,
        SwapMoveMode,
        SwapWeapon,
        SwapFiremode,
        Reload,
        ActivateUltimate,
        ChangeDirection
    }

    // One recorded player input: a queued ICommand, a direct action, or an inventory op.
    internal sealed class NetEntry
    {
        public ICommand Command;
        public DirectAction Action;
        public int Arg;
        public InventoryOp Op;
        public byte[] Data;      // the op's arguments, already encoded
    }

    internal static class Wire
    {
        public static byte[] Build(MsgType type, Action<BinaryWriter> body = null)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((byte)type);
                body?.Invoke(w);
                w.Flush();
                return ms.ToArray();
            }
        }
    }

    // ICommands are plain data classes. Fields are written by reflection; references to game
    // objects are written as ids and resolved on the other machine.
    internal static class CommandCodec
    {
        private const byte TNull = 0, TInt = 1, TBool = 2, TString = 3, TCell = 4, TEnum = 5, TCreature = 6, TObstacle = 7, TDoor = 8, TFloat = 9, TItem = 10, TShort = 11;

        private static readonly Dictionary<string, Type> CommandTypes = typeof(ICommand).Assembly.GetTypes()
            .Where(t => typeof(ICommand).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .ToDictionary(t => t.FullName);

        private static FieldInfo[] Fields(Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.Name).ToArray();

        // Entries are encoded when recorded (item references must name the item where it is at that
        // moment) and decoded just before they're replayed, after the entries before them.
        public static byte[] Encode(NetEntry e)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                WriteEntry(w, e);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteEntry(BinaryWriter w, NetEntry e)
        {
            if (e.Op != 0)
            {
                w.Write((byte)2);
                w.Write((byte)e.Op);
                w.Write(e.Data.Length);
                w.Write(e.Data);
                return;
            }
            if (e.Command == null)
            {
                w.Write((byte)0);
                w.Write((byte)e.Action);
                w.Write(e.Arg);
                return;
            }
            w.Write((byte)1);
            Type type = e.Command.GetType();
            w.Write(type.FullName);
            FieldInfo[] fields = Fields(type);
            w.Write(fields.Length);
            foreach (FieldInfo f in fields)
            {
                w.Write(f.Name);
                WriteValue(w, f.GetValue(e.Command));
            }
        }

        public static NetEntry ReadEntry(BinaryReader r, State state)
        {
            byte kind = r.ReadByte();
            if (kind == 0)
            {
                return new NetEntry { Action = (DirectAction)r.ReadByte(), Arg = r.ReadInt32() };
            }
            if (kind == 2)
            {
                InventoryOp op = (InventoryOp)r.ReadByte();
                return new NetEntry { Op = op, Data = r.ReadBytes(r.ReadInt32()) };
            }
            string typeName = r.ReadString();
            if (!CommandTypes.TryGetValue(typeName, out Type type))
            {
                throw new InvalidDataException("Unknown command type " + typeName);
            }
            // The command constructors only assign fields, so skip them and set the fields directly.
            object cmd = FormatterServices.GetUninitializedObject(type);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                object value = ReadValue(r, state);
                FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (f != null)
                {
                    f.SetValue(cmd, f.FieldType.IsEnum && value is int iv ? Enum.ToObject(f.FieldType, iv) : value);
                }
            }
            return new NetEntry { Command = (ICommand)cmd };
        }

        private static void WriteValue(BinaryWriter w, object v)
        {
            switch (v)
            {
                case null: w.Write(TNull); break;
                case int i: w.Write(TInt); w.Write(i); break;
                case short sh: w.Write(TShort); w.Write(sh); break;
                case bool b: w.Write(TBool); w.Write(b); break;
                case float fl: w.Write(TFloat); w.Write(fl); break;
                case string s: w.Write(TString); w.Write(s); break;
                case CellPosition c: w.Write(TCell); w.Write(c.X); w.Write(c.Y); break;
                case Enum en: w.Write(TEnum); w.Write(Convert.ToInt32(en)); break;
                case Creature cr: w.Write(TCreature); w.Write(cr.CreatureData.UniqueId); break;
                case Door d: w.Write(TDoor); w.Write(d.MapObstacle.UniqueId); break;
                case MapObstacle o: w.Write(TObstacle); w.Write(o.UniqueId); break;
                case BasePickupItem item: w.Write(TItem); ItemSync.WriteItem(w, CoopSession.State, item); break;
                default: throw new NotSupportedException("Can't send command field of type " + v.GetType().FullName);
            }
        }

        private static object ReadValue(BinaryReader r, State state)
        {
            byte tag = r.ReadByte();
            switch (tag)
            {
                case TNull: return null;
                case TInt: return r.ReadInt32();
                case TBool: return r.ReadBoolean();
                case TFloat: return r.ReadSingle();
                case TString: return r.ReadString();
                case TCell: return new CellPosition(r.ReadInt32(), r.ReadInt32());
                case TEnum: return r.ReadInt32();
                case TCreature: return state.Get<Creatures>().GetCreature(r.ReadInt32());
                case TObstacle: return state.Get<MapObstacles>().Get(r.ReadInt32());
                case TDoor: return state.Get<MapObstacles>().Get(r.ReadInt32())?.Door;
                case TItem: return ItemSync.ReadItem(r, state);
                case TShort: return r.ReadInt16();
                default: throw new InvalidDataException("Unknown field tag " + tag);
            }
        }
    }
}
