using System.Linq;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
	public static class AssetBundleHelper
	{
		public static AssetBundle GetAssetBundleFromResources(string fileName, out Stream stream)
		{
			var execAssembly = Assembly.GetExecutingAssembly();

			var resourceName = execAssembly.GetManifestResourceNames()
				.Single(str => str.EndsWith(fileName));

			stream = execAssembly.GetManifestResourceStream(resourceName);
			return AssetBundle.LoadFromStream(stream);
		}
	}
}
