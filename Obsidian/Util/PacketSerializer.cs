﻿using Obsidian.Chat;
using Obsidian.Logging;
using Obsidian.Net;
using Obsidian.Net.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Obsidian.Util
{
    public static class PacketSerializer
    {
        private class Variable
        {
            public Variable(object info, VariableAttribute attribute)
            {
                this.Info = info ?? throw new ArgumentNullException(nameof(info));
                this.Attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
            }

            public object Info { get; }

            public VariableAttribute Attribute { get; }

            public VariableType Type
            {
                get
                {
                    VariableType variableType = Attribute.Type;

                    if (variableType == VariableType.Auto)
                    {
                        Type type = GetValueType();

                        if (type == typeof(bool)) return VariableType.Boolean;
                        else if (type == typeof(byte)) return VariableType.UnsignedByte;
                        else if (type == typeof(byte[])) return VariableType.ByteArray;
                        else if (type == typeof(ChatMessage)) return VariableType.Chat;
                        else if (type == typeof(double)) return VariableType.Double;
                        else if (type == typeof(float)) return VariableType.Float;
                        else if (type == typeof(Guid)) return VariableType.UUID;
                        else if (type == typeof(int)) return VariableType.VarInt;
                        else if (type == typeof(long)) return VariableType.VarLong;
                        else if (type == typeof(Position)) return VariableType.Position;
                        else if (type == typeof(sbyte)) return VariableType.Byte;
                        else if (type == typeof(short)) return VariableType.Short;
                        else if (type == typeof(string)) return VariableType.String;
                        else if (type == typeof(Transform)) return VariableType.Transform;
                        else if (type == typeof(ushort)) return VariableType.UnsignedShort;
                        else if (type.IsEnum) return VariableType.VarInt;
                        else Console.WriteLine($"Failed to get type: {type.Name}");
                    }

                    return variableType;
                }
            }

            public Type GetValueType()
            {
                if (Info is PropertyInfo propertyInfo)
                {
                    return propertyInfo.PropertyType;
                }
                else if (Info is FieldInfo fieldInfo)
                {
                    return fieldInfo.FieldType;
                }

                Debugger.Break(); //this isn't supposed to be hit.
                throw new NotImplementedException();
            }

            public object GetValue(object obj)
            {
                if (Info is PropertyInfo propertyInfo)
                {
                    return propertyInfo.GetValue(obj);
                }
                else if (Info is FieldInfo fieldInfo)
                {
                    return fieldInfo.GetValue(obj);
                }

                Debugger.Break(); //this isn't supposed to be hit.
                throw new NotImplementedException();
            }

            public void SetValue(object obj, object value)
            {
                if (Info is PropertyInfo propertyInfo)
                {
                    propertyInfo.SetValue(obj, value);
                    return;
                }
                else if (Info is FieldInfo fieldInfo)
                {
                    fieldInfo.SetValue(obj, value);
                    return;
                }

                Debugger.Break(); //this isn't supposed to be hit.
                throw new NotImplementedException();
            }
        }

        private static async Task<object> ReadAsync(MinecraftStream stream, Variable var)
        {
            switch (var.Type)
            {
                case VariableType.Int: return await stream.ReadIntAsync();
                case VariableType.Long: return await stream.ReadLongAsync();
                case VariableType.VarInt: return await stream.ReadVarIntAsync();
                case VariableType.VarLong: return await stream.ReadVarLongAsync();
                case VariableType.UnsignedByte: return await stream.ReadUnsignedByteAsync();
                case VariableType.Byte: return await stream.ReadByteAsync();
                case VariableType.Short: return await stream.ReadShortAsync();
                case VariableType.UnsignedShort: return await stream.ReadUnsignedShortAsync();
                case VariableType.String: return await stream.ReadStringAsync();
                case VariableType.Array: return await stream.ReadUInt8ArrayAsync(var.Attribute.Size);
                case VariableType.Position: return await stream.ReadPositionAsync();
                case VariableType.Boolean: return await stream.ReadBooleanAsync();
                case VariableType.Float: return await stream.ReadFloatAsync();
                case VariableType.Double: return await stream.ReadDoubleAsync();
                case VariableType.Transform: return await stream.ReadTransformAsync();

                default:
                case VariableType.List: logger.LogWarning("type not supported..."); return null;//TODO: Add list VariableType
            }
        }

        private static async Task WriteAsync(MinecraftStream stream, Variable var, object value)
        {
            switch (var.Type)
            {
                case VariableType.Int: await stream.WriteIntAsync((int)value); break;
                case VariableType.Long: await stream.WriteLongAsync((long)value); break;
                case VariableType.VarInt: await stream.WriteVarIntAsync((int)value); break;
                case VariableType.VarLong: await stream.WriteVarLongAsync((long)value); break;
                case VariableType.UnsignedByte: await stream.WriteUnsignedByteAsync((byte)value); break;
                case VariableType.Byte: await stream.WriteByteAsync((sbyte)value); break;
                case VariableType.Short: await stream.WriteShortAsync((short)value); break;
                case VariableType.UnsignedShort: await stream.WriteUnsignedShortAsync((ushort)value); break;
                case VariableType.String: await stream.WriteStringAsync((string)value); break;
                case VariableType.Position: await stream.WritePositionAsync((Position)value); break;
                case VariableType.Boolean: await stream.WriteBooleanAsync((bool)value); break;
                case VariableType.Float: await stream.WriteFloatAsync((float)value); break;
                case VariableType.Double: await stream.WriteDoubleAsync((double)value); break;
                case VariableType.Chat: await stream.WriteChatAsync((ChatMessage)value); break;
                case VariableType.ByteArray: await stream.WriteAsync((byte[])value); break;

                default:
                case VariableType.Transform: //TODO: add writing transforms
                case VariableType.Array: //TODO: add writing int arrays
                case VariableType.List: logger.LogWarning($"Failed to read type:{var.Type}..."); break; //TODO: Add list VariableType
            }
        }

        private static List<Variable> GetVariables<T>(T packet) where T : Packet
        {
            var variables = new List<Variable>();

            foreach (PropertyInfo property in packet.GetType().GetProperties())
            {
                object[] attributes = property.GetCustomAttributes(typeof(VariableAttribute), false);

                if (attributes.Length != 1)
                {
                    continue;
                }

                variables.Add(new Variable(property, (VariableAttribute)attributes[0]));
            }

            foreach (FieldInfo field in packet.GetType().GetFields())
            {
                object[] attributes = field.GetCustomAttributes(typeof(VariableAttribute), false);

                if (attributes.Length != 1)
                {
                    continue;
                }

                variables.Add(new Variable(field, (VariableAttribute)attributes[0]));
            }

            return variables;
        }

        public static async Task SerializeAsync(Packet packet, MinecraftStream outStream)
        {
            List<Variable> variables = GetVariables(packet);

            using (var stream = new MinecraftStream())
            {
                foreach (Variable variable in variables.OrderBy(x => x.Attribute.Order))
                {

                    object value = variable.GetValue(packet);

                    Console.WriteLine(variable.Type);

                    try
                    {
                        if (packet is EncryptionRequest && variable.Type == VariableType.String)
                        {
                            await stream.WriteStringAsync(string.Empty);
                            continue;
                        }

                        await WriteAsync(stream, variable, value);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message + ": " + e.StackTrace);
                    }
                }

                var data = stream.ToArray();

                int packetLength = data.Length + packet.PacketId.GetVarintLength();

                await outStream.WriteVarIntAsync(packetLength);
                await outStream.WriteVarIntAsync(packet.PacketId);
                await outStream.WriteAsync(data);
            }
        }

        private static Logger logger = new Logger("Serialzer", LogLevel.Debug);

        public static async Task<T> DeserializeAsync<T>(T packet) where T : Packet
        {
            List<Variable> variables = GetVariables(packet);

            using (var stream = new MinecraftStream(packet.PacketData))
            {
                foreach (Variable variable in variables)
                {
                    var value = await ReadAsync(stream, variable);

                    variable.SetValue(packet, value);
                }
            }

            return (T)Convert.ChangeType(packet, typeof(T));
        }

        public static async Task<Packet> ReadFromStreamAsync(MinecraftStream stream)
        {
            int length = await stream.ReadVarIntAsync();
            byte[] receivedData = new byte[length];

            await stream.ReadAsync(receivedData, 0, length);

            int packetId = 0;
            byte[] packetData = new byte[0];

            using (var packetStream = new MinecraftStream(receivedData))
            {
                try
                {
                    packetId = await packetStream.ReadVarIntAsync();
                    int arlen = 0;

                    if (length - packetId.GetVarintLength() > -1)
                        arlen = length - packetId.GetVarintLength();

                    packetData = new byte[arlen];
                    await packetStream.ReadAsync(packetData, 0, packetData.Length);
                }
                catch
                {
                    throw;
                }
            }

            return new EmptyPacket(packetId, packetData);
        }
    }
}