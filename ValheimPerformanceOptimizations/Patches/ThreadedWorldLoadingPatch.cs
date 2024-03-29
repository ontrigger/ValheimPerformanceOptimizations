﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using ValheimPerformanceOptimizations.Patches;

namespace ValheimPerformanceOptimizations
{
	/// <summary>
	/// Loading big worlds is slow and can partially be called threaded to speed it up.
	/// </summary>
	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
	public static class ThreadedWorldLoadingPatch
	{
		private static bool Prefix(ZDOMan __instance, BinaryReader reader, int version)
		{
			ZNetSceneObjectManagementPatch.CreateRemoveHack = true;
			// only patch the current data version to not risk any world file breaking
			if (version != 29)
			{
				var message = $"ZDOMan.Load() unsupported data version: {version}. Fallback to original method";
				ValheimPerformanceOptimizations.Logger.LogInfo(message);
				return true;
			}

			reader.ReadInt64();
			var num = reader.ReadUInt32();
			var num2 = reader.ReadInt32();
			ZDOPool.Release(__instance.m_objectsByID);
			__instance.m_objectsByID.Clear();
			__instance.ResetSectorArray();
			ZLog.Log("Loading " + num2 + " zdos , my id " + __instance.m_myid + " data version:" + version);

			/* --- begin changes --- */

			var packages = new Tuple<ZDO, byte[]>[num2];

			// don't load zDO data immediately
			for (var i = 0; i < num2; i++)
			{
				var zDO = ZDOPool.Create(__instance);
				zDO.m_uid = new ZDOID(reader);
				var count = reader.ReadInt32();
				var data = reader.ReadBytes(count);
				__instance.m_objectsByID.Add(zDO.m_uid, zDO);

				// store the data
				packages[i] = new Tuple<ZDO, byte[]>(zDO, data);

				if (zDO.m_uid.userID == __instance.m_myid && zDO.m_uid.id >= num)
				{
					num = zDO.m_uid.id + 1;
				}
			}

			// now load zDO threaded
			Parallel.ForEach(Partitioner.Create(0, num2), range =>
			{
				var zPackage = new ZPackage();

				for (var j = range.Item1; j < range.Item2; j++)
				{
					var zDO = packages[j].Item1;
					var data = packages[j].Item2;

					zPackage.Load(data);
					zDO.Load(zPackage, version);
					zDO.SetOwner(0L);
				}
			});

			// AddToSector can only be called after zDO.Load() and is not thread save
			for (var i = 0; i < num2; i++)
			{
				var zDO = packages[i].Item1;
				__instance.AddToSector(zDO, zDO.GetSector());
			}

			/* --- change end --- */

			__instance.m_deadZDOs.Clear();
			var num3 = reader.ReadInt32();
			for (var j = 0; j < num3; j++)
			{
				var key = new ZDOID(reader.ReadInt64(), reader.ReadUInt32());
				var value = reader.ReadInt64();
				__instance.m_deadZDOs.Add(key, value);
				if (key.userID == __instance.m_myid && key.id >= num)
				{
					num = key.id + 1;
				}
			}

			__instance.CapDeadZDOList();
			ZLog.Log("Loaded " + __instance.m_deadZDOs.Count + " dead zdos");
			__instance.RemoveOldGeneratedZDOS();
			__instance.m_nextUid = num;
			
			ZNetSceneObjectManagementPatch.CreateRemoveHack = false;

			return false;
		}
	}
}
