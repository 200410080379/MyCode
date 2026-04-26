using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MiniLink
{
    /// <summary>
    /// 网络写入器 - 轻量级二进制序列化
    /// </summary>
    public class NetworkWriter : IDisposable
    {
        private List<byte> buffer = new List<byte>(1024);
        private bool returnedToPool;

        public void WriteByte(byte value) { buffer.Add(value); }
        public void WriteByte(int value) { buffer.Add((byte)value); }

        public void WriteBool(bool value) { buffer.Add(value ? (byte)1 : (byte)0); }

        public void WriteShort(short value)
        {
            var bytes = BitConverter.GetBytes(value);
            buffer.AddRange(bytes);
        }

        public void WriteInt(int value)
        {
            var bytes = BitConverter.GetBytes(value);
            buffer.AddRange(bytes);
        }

        public void WriteFloat(float value)
        {
            var bytes = BitConverter.GetBytes(value);
            buffer.AddRange(bytes);
        }

        public void WriteLong(long value)
        {
            var bytes = BitConverter.GetBytes(value);
            buffer.AddRange(bytes);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt(0);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt(bytes.Length);
            buffer.AddRange(bytes);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteInt(0);
                return;
            }
            WriteInt(value.Length);
            buffer.AddRange(value);
        }

        public void WriteVector2(Vector2 v)
        {
            WriteFloat(v.x);
            WriteFloat(v.y);
        }

        public void WriteVector3(Vector3 v)
        {
            WriteFloat(v.x);
            WriteFloat(v.y);
            WriteFloat(v.z);
        }

        public void WriteQuaternion(Quaternion q)
        {
            WriteFloat(q.x);
            WriteFloat(q.y);
            WriteFloat(q.z);
            WriteFloat(q.w);
        }

        public byte[] ToArray()
        {
            return buffer.ToArray();
        }

        public void Reset()
        {
            buffer.Clear();
            returnedToPool = false;
        }

        public int Position => buffer.Count;

        public void Dispose()
        {
            if (!returnedToPool)
            {
                Pool.Return(this);
            }
        }

        /// <summary>
        /// 对象池（参考 Mirror WriterPool）
        /// </summary>
        public static class Pool
        {
            private static readonly Stack<NetworkWriter> pool = new Stack<NetworkWriter>();

            public static NetworkWriter Get()
            {
                var writer = pool.Count > 0 ? pool.Pop() : new NetworkWriter();
                writer.returnedToPool = false;
                return writer;
            }

            public static void Return(NetworkWriter writer)
            {
                if (writer == null || writer.returnedToPool) return;
                writer.buffer.Clear();
                writer.returnedToPool = true;
                pool.Push(writer);
            }
        }
    }

    /// <summary>
    /// 网络读取器 - 轻量级二进制反序列化
    /// </summary>
    public class NetworkReader : IDisposable
    {
        private byte[] buffer;
        private int position;

        public NetworkReader(byte[] data)
        {
            buffer = data;
            position = 0;
        }

        public byte ReadByte()
        {
            return buffer[position++];
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public short ReadShort()
        {
            var value = BitConverter.ToInt16(buffer, position);
            position += 2;
            return value;
        }

        public int ReadInt()
        {
            var value = BitConverter.ToInt32(buffer, position);
            position += 4;
            return value;
        }

        public float ReadFloat()
        {
            var value = BitConverter.ToSingle(buffer, position);
            position += 4;
            return value;
        }

        public long ReadLong()
        {
            var value = BitConverter.ToInt64(buffer, position);
            position += 8;
            return value;
        }

        public string ReadString()
        {
            int length = ReadInt();
            if (length == 0) return null;
            var value = Encoding.UTF8.GetString(buffer, position, length);
            position += length;
            return value;
        }

        public byte[] ReadBytes()
        {
            int length = ReadInt();
            if (length == 0) return null;
            var value = new byte[length];
            Array.Copy(buffer, position, value, 0, length);
            position += length;
            return value;
        }

        public Vector2 ReadVector2()
        {
            return new Vector2(ReadFloat(), ReadFloat());
        }

        public Vector3 ReadVector3()
        {
            return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        }

        public Quaternion ReadQuaternion()
        {
            return new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        public int Position => position;
        public int Length => buffer.Length;
        public bool Finished => position >= buffer.Length;

        public void Dispose()
        {
            // Reader 不持有非托管资源，保留 using 语义即可。
        }
    }

    #region Message Structures

    /// <summary>Command消息：客户端→服务端</summary>
    public struct CommandMessage
    {
        public uint netId;
        public string methodHash;
        public int channel;
        public bool requiresAuthority;
        public byte[] args;
    }

    /// <summary>ClientRpc消息：服务端→所有客户端</summary>
    public struct ClientRpcMessage
    {
        public uint netId;
        public string methodHash;
        public int channel;
        public bool includeOwner;
        public byte[] args;
    }

    /// <summary>TargetRpc消息：服务端→指定客户端</summary>
    public struct TargetRpcMessage
    {
        public uint netId;
        public string methodHash;
        public string targetConnId;
        public int channel;
        public byte[] args;
    }

    /// <summary>SyncVar同步消息</summary>
    public struct SyncVarMessage
    {
        public uint netId;
        public string component;
        public int dirtyMask;
        public byte[] payload;
    }

    /// <summary>快照同步消息</summary>
    public struct SnapshotMessage
    {
        public uint netId;
        public long remoteTick;
        public float remoteTime;
        public byte[] stateData;
    }

    /// <summary>帧同步输入消息</summary>
    public struct InputFrameMessage
    {
        public uint netId;
        public int frame;
        public byte[] inputData;
    }

    /// <summary>生成对象消息</summary>
    public struct SpawnMessage
    {
        public uint netId;
        public int prefabHash;
        public string ownerConnId;
        public byte[] initialState;
    }

    /// <summary>房间状态通知</summary>
    public struct RoomStateMessage
    {
        public string roomId;
        public string roomName;
        public string state; // WAITING, READY, PLAYING, FINISHED
        public int maxPlayers;
        public string hostId;
        public PlayerInfoMessage[] players;
    }

    /// <summary>玩家信息</summary>
    public struct PlayerInfoMessage
    {
        public string playerId;
        public string nickname;
        public string avatarUrl;
        public bool isReady;
        public bool isHost;
        public int slotIndex;
    }

    #endregion
}
