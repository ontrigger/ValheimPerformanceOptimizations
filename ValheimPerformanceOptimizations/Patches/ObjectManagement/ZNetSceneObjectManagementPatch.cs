using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement
{
	[HarmonyPatch]
	public static partial class ZNetSceneObjectManagementPatch
	{
		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))]
		[HarmonyPrefix]
		private static bool ZNetScene_CreateDestroyObjects_Prefix(ZNetScene __instance)
		{
			return CreateDestroyObjects(__instance);
		}

		[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector))]
		[HarmonyPostfix]
		private static void ZDOMan_AddToSector_Postfix(ZDO zdo)
		{
			InvalidatedZdoIds.Remove(zdo.m_uid);
			MarkAreaMembershipDirty(zdo.m_uid);
		}

		[HarmonyPatch(typeof(ZDO), nameof(ZDO.InvalidateSector))]
		[HarmonyPostfix]
		private static void ZDO_InvalidateSector_Postfix(ZDO __instance)
		{
			if (IsPortal(__instance)) { return; }

			var id = __instance.m_uid;
			InvalidatedZdoIds.Add(id);

			// GetSector() continues to report the old position after invalidation, so
			// retain this explicit state until AddToSector makes the ZDO valid again.
			MarkAreaMembershipDirty(id);
		}

		[HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
		[HarmonyPostfix]
		private static void ZDOMan_HandleDestroyedZDO_Postfix(ZDOID uid)
		{
			PendingMembershipIds.Remove(uid);
			PendingRemovalObjects.Remove(uid);
			InvalidatedZdoIds.Remove(uid);
			RemoveTrackedMembership(uid);
		}

		[HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
		[HarmonyPostfix]
		private static void ZDO_Deserialize_Postfix(ZDO __instance)
		{
			// area membership can change after deserialize
			MarkAreaMembershipDirty(__instance.m_uid);
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance))]
		[HarmonyPostfix]
		private static void ZNetScene_AddInstance_Postfix(ZDO zdo)
		{
			MarkAreaMembershipDirty(zdo.m_uid);
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
		[HarmonyPrefix]
		private static bool ZNetScene_Destroy_Prefix(ZNetScene __instance, GameObject go)
		{
			var zNetView = go.GetComponent<ZNetView>();
			if (zNetView && zNetView.GetZDO() != null)
			{
				var zdo = zNetView.GetZDO();
				zNetView.ResetZDO();
				__instance.m_instances.Remove(zdo);
				if (zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				else
				{
					// vanilla would recreate this zdo on the next scan because it never actually got destroyed serverside
					MarkAreaMembershipDirty(zdo.m_uid);
				}
			}

			Object.Destroy(go);
			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
		[HarmonyPrefix]
		private static void ZNetScene_Shutdown_Prefix()
		{
			ResetObjectCache();
		}
	}
}
