using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
	public static class AssetBundleHelper
	{
		public static AssetBundle GetAssetBundleFromResources(string fileName)
		{
			var execAssembly = Assembly.GetExecutingAssembly();

			var resourceName = execAssembly.GetManifestResourceNames()
				.Single(str => str.EndsWith(fileName));

			using (var stream = execAssembly.GetManifestResourceStream(resourceName))
			{
				return AssetBundle.LoadFromStream(stream);
			}
		}
	}
}
