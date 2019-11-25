using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nordeus.Nuget.Utility;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace NugetForUnity
{
	[InitializeOnLoad]
	public static class SystemProxy
	{
		static SystemProxy()
		{
			if (!IsPlayingOrWillChangePlaymode && !UnityEditorInternal.InternalEditorUtility.inBatchMode)
			{
				// restore packages - this will be called EVERY time the project is loaded or a code-file changes.
				NugetHelper.Restore();
			}
			EditorApplication.projectWindowItemOnGUI += ReplaceFolderIcon;
		}

		public static string CurrentDir => Application.dataPath;
		public static bool IsPlayingOrWillChangePlaymode => EditorApplication.isPlayingOrWillChangePlaymode;

		public static string UnityVersion => Application.unityVersion;

		public static void Log(string message)
		{
			Debug.Log(message);
		}

		public static void LogWarning(string message)
		{
			Debug.LogWarning(message);
		}

		public static void LogError(string message)
		{
			Debug.LogError(message);
		}

		public static void RefreshAssets()
		{
			 AssetDatabase.Refresh();
		}


		public static int GetApiCompatibilityLevel()
		{
			return (int)PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup);
		}

		public static void DisplayProgress(string title, string info, float progress)
		{
			EditorUtility.DisplayProgressBar(title, info, progress);
		}

		public static void ClearProgress()
		{
			EditorUtility.ClearProgressBar();
		}

		public static void DownloadAndSetIcon(NugetPackage package, string url)
		{
			StartCoroutine(DownloadAndSetIconRoutine(package, url));
		}

		
		private static readonly List<IEnumerator> activeEnumerators = new List<IEnumerator>();
		private static readonly List<IEnumerator> toRemove = new List<IEnumerator>();

		private static void StartCoroutine(IEnumerator enumerator)
		{
			if (!enumerator.MoveNext()) return;

			if (activeEnumerators.Count == 0)
			{
				EditorApplication.update -= RunCoroutines;
				EditorApplication.update += RunCoroutines;
			}

			activeEnumerators.Add(enumerator);
		}

		private static void RunCoroutines()
		{
			foreach (var enumerator in activeEnumerators)
			{
				if (!enumerator.MoveNext()) toRemove.Add(enumerator);
			}

			foreach (var enumerator in toRemove)
			{
				activeEnumerators.Remove(enumerator);
			}

			toRemove.Clear();

			if (activeEnumerators.Count == 0)
			{
				EditorApplication.update -= RunCoroutines;
			}
		}

		/// <summary>
		/// Downloads an image at the given URL and converts it to a Unity Texture2D.
		/// </summary>
		/// <param name="package">The package to set the icon on</param>
		/// <param name="url">The URL of the image to download.</param>
		private static IEnumerator DownloadAndSetIconRoutine(NugetPackage package, string url)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			var fromCache = false;
			if (ExistsInDiskCache(url))
			{
				url = "file:///" + GetFilePath(url);
				fromCache = true;
			}

			Texture2D result = null;

			using (var request = UnityWebRequestTexture.GetTexture(url, false))
			{
				const int timeout = 5;
				request.timeout = timeout;
				// Since we are handling coroutines by ourselves we can't yield return this directly
				request.SendWebRequest();
				while (!request.isDone && stopwatch.ElapsedMilliseconds < timeout * 1000) yield return null;

				if (request.isDone && !request.isNetworkError && !request.isHttpError)
				{
					result = DownloadHandlerTexture.GetContent(request);
				}
				else if (!string.IsNullOrEmpty(request.error))
				{
					LogWarning($"Request {url} error after {stopwatch.ElapsedMilliseconds} ms: {request.error}");
				}
				else LogWarning($"Request {url} timed out after {stopwatch.ElapsedMilliseconds} ms");


				if (result != null && !fromCache)
				{
					CacheTextureOnDisk(url, request.downloadHandler.data);
				}
			}

			package.Icon = result;
		}

		private static void CacheTextureOnDisk(string url, byte[] bytes)
		{
			var diskPath = GetFilePath(url);
			File.WriteAllBytes(diskPath, bytes);
		}

		private static bool ExistsInDiskCache(string url)
		{
			return File.Exists(GetFilePath(url));
		}

		private static string GetFilePath(string url)
		{
			return Path.Combine(Application.temporaryCachePath, GetHash(url));
		}

		private static string GetHash(string s)
		{
			if (string.IsNullOrEmpty(s))
				return null;
			var md5 = new MD5CryptoServiceProvider();
			var data = md5.ComputeHash(Encoding.Default.GetBytes(s));
			var sBuilder = new StringBuilder();
			for (var i = 0; i < data.Length; i++)
			{
				sBuilder.Append(data[i].ToString("x2"));
			}

			return sBuilder.ToString();
		}

		private static readonly HashSet<string> pathsWithLinks = new HashSet<string>();
		private static bool arePathsInitialized;
		private static Texture2D linkIcon;

		private static void ReplaceFolderIcon(string guid, Rect rect)
		{
			if (!arePathsInitialized)
			{
				arePathsInitialized = true;
				var repoAbsPath = NugetHelper.NugetConfigFile.RepositoryPath.Replace("\\", "/");
				var assetsIndex = repoAbsPath.IndexOf("/Assets/", StringComparison.Ordinal);
				if (assetsIndex < 0) return;
				assetsIndex++;
				foreach (var dir in Directory.EnumerateDirectories(repoAbsPath))
				{
					if (!SymbolicLink.Exists(dir)) continue;
					var g = AssetDatabase.AssetPathToGUID(dir.Substring(assetsIndex));
					if (!string.IsNullOrEmpty(g)) pathsWithLinks.Add(g);
				}

				if (pathsWithLinks.Count > 0)
				{
					var repoPath = repoAbsPath.Substring(assetsIndex).Trim('/');
					while (repoPath != null && repoPath != "Assets")
					{
						var g = AssetDatabase.AssetPathToGUID(repoPath);
						if (!string.IsNullOrEmpty(g)) pathsWithLinks.Add(g);
						repoPath = Path.GetDirectoryName(repoPath);
					}
				}
			}
			
			if (!pathsWithLinks.Contains(guid)) return;
			
			if (linkIcon == null) linkIcon = Resources.Load<Texture2D>("link16");
			if (linkIcon == null) return;

			var smaller = rect.width < rect.height ? rect.width : rect.height;
			var d = smaller > 16 ? 16 : 10;

			if (smaller > 16) smaller = smaller / 1.6f + 10;
			
			rect = new Rect(rect.x + smaller - d + 2, rect.y + smaller - d, d, d);

			GUI.DrawTexture(rect, linkIcon);
		}
	}
}
