using Agents;
using API;
using Enemies;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using SNetwork;
using UnityEngine;

// TODO(randomuserhi): Add MTFO support to not add too many network hooks
// TODO(randomuserhi): Add an aggreement packet on player join to prevent sending bad packets all the time
namespace KillIndicatorFix.Patches {
    [HarmonyPatch]
    internal static class Network {
        public static void SendHitIndicator(Agent target, byte limbID, PlayerAgent player, bool hitWeakspot, bool willDie, Vector3 position, bool hitArmor = false) {
            // client cannot send hit indicators
            if (!SNet.IsMaster) return;
            // player cannot send hit indicators to self
            if (player == PlayerManager.GetLocalPlayerAgent()) return;
            // check player is not a bot
            if (player.Owner.IsBot) return;

            SNet_ChannelType channelType = SNet_ChannelType.SessionOrderCritical;
            SNet.GetSendSettings(ref channelType, out _, out SNet_SendQuality quality, out int channel);
            Il2CppSystem.Collections.Generic.List<SNet_Player> il2cppList = new(1);
            il2cppList.Add(player.Owner);

            const int sizeOfHeader = sizeof(ushort) + sizeof(uint) + 1 + sizeof(int);
            const int sizeOfContent = sizeof(ushort) + 3 + BitHelper.SizeOfHalfVector3 + 1;

            int index = 0;
            byte[] packet = new byte[sizeOfHeader + sizeOfContent];
            BitHelper.WriteBytes(repKey, packet, ref index);
            BitHelper.WriteBytes(magickey, packet, ref index);
            BitHelper.WriteBytes(msgtype, packet, ref index);
            BitHelper.WriteBytes(sizeOfContent, packet, ref index);

            BitHelper.WriteBytes((ushort)(target.m_replicator.Key + 1), packet, ref index);
            BitHelper.WriteBytes(limbID, packet, ref index);
            BitHelper.WriteBytes(hitWeakspot, packet, ref index);
            BitHelper.WriteBytes(willDie, packet, ref index);
            BitHelper.WriteHalf(position, packet, ref index);
            BitHelper.WriteBytes(hitArmor, packet, ref index);
            SNet.Core.SendBytes(packet, quality, channel, il2cppList);
            APILogger.Debug($"Sent hit marker to {player.PlayerName}");
        }

        private static byte msgtype = 173;
        private static uint magickey = 10992881;
        private static ushort repKey = 0xFFFB; // make sure doesnt clash with GTFO-API

        // https://github.com/Kasuromi/GTFO-API/blob/main/GTFO-API/Patches/SNet_Replication_Patches.cs#L56
        [HarmonyPatch(typeof(SNet_Replication), nameof(SNet_Replication.RecieveBytes))]
        [HarmonyWrapSafe]
        [HarmonyPrefix]
        private static bool RecieveBytes_Prefix(Il2CppStructArray<byte> bytes, uint size, ulong messagerID) {
            if (size < 12) return true;

            // The implicit constructor duplicates the memory, so copying it once and using that is best
            byte[] _bytesCpy = bytes;

            ushort replicatorKey = BitConverter.ToUInt16(_bytesCpy, 0);
            if (repKey == replicatorKey) {
                uint receivedMagicKey = BitConverter.ToUInt32(bytes, sizeof(ushort));
                if (receivedMagicKey != magickey) {
                    APILogger.Debug($"[Networking] Magic key is incorrect.");
                    return true;
                }

                byte receivedMsgtype = bytes[sizeof(ushort) + sizeof(uint)];
                if (receivedMsgtype != msgtype) {
                    APILogger.Debug($"[Networking] msg type is incorrect. {receivedMsgtype} {msgtype}");
                    return true;
                }


                int msgsize = BitConverter.ToInt32(bytes, sizeof(ushort) + sizeof(int) + 1);
                byte[] message = new byte[msgsize];
                Array.Copy(bytes, sizeof(ushort) + sizeof(uint) + 1 + sizeof(int), message, 0, msgsize);

                int index = 0;
                ushort agentRepKey = BitHelper.ReadUShort(message, ref index);
                byte limbID = BitHelper.ReadByte(message, ref index);
                bool hitWeakspot = BitHelper.ReadBool(message, ref index);
                bool willDie = BitHelper.ReadBool(message, ref index);
                Vector3 position = BitHelper.ReadHalfVector3(message, ref index);
                bool hitArmor = BitHelper.ReadBool(message, ref index);

                SNetStructs.pReplicator pRep;
                pRep.keyPlusOne = agentRepKey;
                pAgent _agent;
                _agent.pRep = pRep;
                _agent.TryGet(out Agent agent);
                EnemyAgent? targetEnemy = agent.TryCast<EnemyAgent>();
                if (targetEnemy != null) {
                    APILogger.Debug("Received hit indicator for enemy.");
                    Dam_EnemyDamageLimb dam = targetEnemy.Damage.DamageLimbs[limbID];
                    Kill.sentryShot = true;
                    dam.ShowHitIndicator(hitWeakspot, willDie, position, hitArmor);
                    Kill.sentryShot = false;
                    return false;
                }
                PlayerAgent? targetPlayer = agent.TryCast<PlayerAgent>();
                if (targetPlayer != null) {
                    APILogger.Debug("Received hit indicator for player.");
                    GuiManager.CrosshairLayer.PopFriendlyTarget();
                    return false;
                }

                APILogger.Debug("Received hit indicator packet but could not get player / enemy agent. This should not happen.");

                return false;
            }
            return true;
        }
    }
}
