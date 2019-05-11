﻿namespace NugetForUnity
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using UnityEditor;
	using UnityEngine;
	using UnityEngine.Networking;

	/// <summary>
	/// Represents the NuGet Package Manager Window in the Unity Editor.
	/// </summary>
	public class NugetWindow : EditorWindow
	{
		/// <summary>
		/// True when the NugetWindow has initialized. This is used to skip time-consuming reloading operations when the assembly is reloaded.
		/// </summary>
		[SerializeField]
		private bool hasRefreshed = false;

		/// <summary>
		/// The current position of the scroll bar in the GUI.
		/// </summary>
		private Vector2 scrollPosition;

		/// <summary>
		/// The list of NugetPackages available to install.
		/// </summary>
		[SerializeField]
		private List<NugetPackage> availablePackages = new List<NugetPackage>();

		/// <summary>
		/// The list of package updates available, based on the already installed packages.
		/// </summary>
		[SerializeField]
		private List<NugetPackage> updatePackages = new List<NugetPackage>();

		/// <summary>
		/// The filtered list of package updates available.
		/// </summary>
		private List<NugetPackage> filteredUpdatePackages = new List<NugetPackage>();

		/// <summary>
		/// True to show all old package versions.  False to only show the latest version.
		/// </summary>
		private bool showAllOnlineVersions;

		/// <summary>
		/// True to show beta and alpha package versions.  False to only show stable versions.
		/// </summary>
		private bool showOnlinePrerelease;

		/// <summary>
		/// True to show all old package versions.  False to only show the latest version.
		/// </summary>
		private bool showAllUpdateVersions;

		/// <summary>
		/// True to show beta and alpha package versions.  False to only show stable versions.
		/// </summary>
		private bool showPrereleaseUpdates;

		/// <summary>
		/// The width to use for the install/uninstall/update/downgrade button
		/// </summary>
		private readonly GUILayoutOption installButtonWidth = GUILayout.Width(180);

		/// <summary>
		/// The height to use for the install/uninstall/update/downgrade button
		/// </summary>
		private readonly GUILayoutOption installButtonHeight = GUILayout.Height(27);

		/// <summary>
		/// The search term to search the online packages for.
		/// </summary>
		private string onlineSearchTerm = "Search";

		/// <summary>
		/// The search term to search the installed packages for.
		/// </summary>
		private string installedSearchTerm = "Search";

		/// <summary>
		/// The search term in progress while it is being typed into the search box.
		/// We wait until the Enter key or Search button is pressed before searching in order
		/// to match the way that the Online and Updates searches work.
		/// </summary>
		private string installedSearchTermEditBox = "Search";

		/// <summary>
		/// The search term to search the update packages for.
		/// </summary>
		private string updatesSearchTerm = "Search";

		/// <summary>
		/// The number of packages to get from the request to the server.
		/// </summary>
		private const int numberToGet = 15;

		/// <summary>
		/// The number of packages to skip when requesting a list of packages from the server.  This is used to get a new group of packages.
		/// </summary>
		[SerializeField]
		private int numberToSkip;

		/// <summary>
		/// The currently selected tab in the window.
		/// </summary>
		private int currentTab;

		/// <summary>
		/// The titles of the tabs in the window.
		/// </summary>
		private readonly string[] tabTitles = { "Online", "Installed", "Updates" };

		/// <summary>
		/// The default icon to display for packages.
		/// </summary>
		[SerializeField]
		private Texture2D defaultIcon;

		/// <summary>
		/// Used to keep track of which packages the user has opened the clone window on.
		/// </summary>
		private readonly HashSet<NugetPackage> openCloneWindows = new HashSet<NugetPackage>();

		private IEnumerable<NugetPackage> FilteredInstalledPackages
		{
			get
			{
				if (installedSearchTerm == "Search")
					return NugetHelper.InstalledPackages;

				return NugetHelper.InstalledPackages.Where(x => x.Id.ToLower().Contains(installedSearchTerm) || x.Title.ToLower().Contains(installedSearchTerm)).ToList();
			}
		}

		/// <summary>
		/// Opens the NuGet Package Manager Window.
		/// </summary>
		[MenuItem("NuGet/Manage NuGet Packages", false, 0)]
		protected static void DisplayNugetWindow()
		{
			GetWindow<NugetWindow>();
		}

		/// <summary>
		/// Restores all packages defined in packages.config
		/// </summary>
		[MenuItem("NuGet/Restore Packages", false, 1)]
		protected static void RestorePackages()
		{
			NugetHelper.Restore();
		}

		/// <summary>
		/// Displays the version number of NuGetForUnity.
		/// </summary>
		[MenuItem("NuGet/Version " + NugetPreferences.NuGetForUnityVersion, false, 10)]
		protected static void DisplayVersion()
		{
			var assembly = System.Reflection.Assembly.GetAssembly(typeof(EditorWindow));
			var preferencesWindow = assembly.GetType("UnityEditor.PreferencesWindow");
			var preferencesWindowSection = assembly.GetType("UnityEditor.PreferencesWindow+Section"); // access nested class via + instead of .         

			// open the preferences window
			var preferencesEditorWindow = GetWindowWithRect(preferencesWindow, new Rect(100f, 100f, 500f, 400f), true, "Unity Preferences");
			//preferencesEditorWindow.m_Parent.window.m_DontSaveToLayout = true; //<-- Unity's implementation also does this

			// Get the flag to see if custom sections have already been added
			var m_RefreshCustomPreferences = preferencesWindow.GetField("m_RefreshCustomPreferences", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			var refesh = (bool)m_RefreshCustomPreferences.GetValue(preferencesEditorWindow);

			if (refesh)
			{
				// Invoke the AddCustomSections to load all user-specified preferences sections.  This normally isn't done until OnGUI, but we need to call it now to set the proper index
				var addCustomSections = preferencesWindow.GetMethod("AddCustomSections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				addCustomSections.Invoke(preferencesEditorWindow, null);

				// Unity is dumb and doesn't set the flag for having loaded the custom sections INSIDE the AddCustomSections method!  So we must call it manually.
				m_RefreshCustomPreferences.SetValue(preferencesEditorWindow, false);
			}

			// get the List<PreferencesWindow.Section> m_Sections.Count
			var m_Sections = preferencesWindow.GetField("m_Sections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			var list = m_Sections.GetValue(preferencesEditorWindow);
			var sectionList = typeof(List<>).MakeGenericType(preferencesWindowSection);
			var getCount = sectionList.GetProperty("Count").GetGetMethod(true);
			var count = (int)getCount.Invoke(list, null);
			//Debug.LogFormat("Count = {0}", count);

			// Figure out the index of the NuGet for Unity preferences
			var getItem = sectionList.GetMethod("get_Item");
			var nugetIndex = 0;
			for (var i = 0; i < count; i++)
			{
				var section = getItem.Invoke(list, new object[] { i });
				var content = (GUIContent)section.GetType().GetField("content", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).GetValue(section);
				if (content != null && content.text == "NuGet For Unity")
				{
					nugetIndex = i;
					break;
				}
			}
			//Debug.LogFormat("NuGet index = {0}", nugetIndex);

			// set the selected section index
			var selectedSectionIndex = preferencesWindow.GetProperty("selectedSectionIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			var selectedSectionIndexSetter = selectedSectionIndex.GetSetMethod(true);
			selectedSectionIndexSetter.Invoke(preferencesEditorWindow, new object[] { nugetIndex });
			//var selectedSectionIndexGetter = selectedSectionIndex.GetGetMethod(true);
			//object index = selectedSectionIndexGetter.Invoke(preferencesEditorWindow, null);
			//Debug.LogFormat("Selected Index = {0}", index);
		}

		/// <summary>
		/// Checks/launches the Releases page to update NuGetForUnity with a new version.
		/// </summary>
		[MenuItem("NuGet/Check for Updates...", false, 10)]
		protected static void CheckForUpdates()
		{
			const string url = "https://github.com/GlitchEnzo/NuGetForUnity/releases";
			using (var request = UnityWebRequest.Get(url))
			{
				request.SendWebRequest();

				NugetHelper.LogVerbose("HTTP GET {0}", url);
				while (!request.isDone)
				{
					EditorUtility.DisplayProgressBar("Checking updates", null, 0.0f);
				}
				EditorUtility.ClearProgressBar();

				string latestVersion = null;
				string latestVersionDownloadUrl = null;

				string response = null;
				if (!request.isNetworkError && !request.isHttpError)
				{
					response = request.downloadHandler.text;
				}

				if (response != null)
				{
					latestVersion = GetLatestVersonFromReleasesHtml(response, out latestVersionDownloadUrl);
				}

				if (latestVersion == null)
				{
					EditorUtility.DisplayDialog(
							"Unable to Determine Updates",
							$"Couldn't find release information at {url}.",
							"OK");
					return;
				}

				var current = new NugetPackageIdentifier("NuGetForUnity", NugetPreferences.NuGetForUnityVersion);
				var latest = new NugetPackageIdentifier("NuGetForUnity", latestVersion);
				if (current >= latest)
				{
					EditorUtility.DisplayDialog(
							"No Updates Available",
							$"Your version of NuGetForUnity is up to date.\nVersion {NugetPreferences.NuGetForUnityVersion}.",
							"OK");
					return;
				}

				// New version is available. Give user options for installing it.
				switch (EditorUtility.DisplayDialogComplex(
						"Update Available",
						$"Current Version: {NugetPreferences.NuGetForUnityVersion}\nLatest Version: {latestVersion}",
						"Install Latest",
						"Open Releases Page",
						"Cancel"))
				{
					case 0: Application.OpenURL(latestVersionDownloadUrl); break;
					case 1: Application.OpenURL(url); break;
					case 2: break;
				}
			}
		}

		private static string GetLatestVersonFromReleasesHtml(string response, out string url)
		{
			var hrefRegex = new Regex(@"<a href=""(?<url>.*NuGetForUnity\.(?<version>\d+\.\d+\.\d+)\.unitypackage)""");
			var match = hrefRegex.Match(response);
			if (!match.Success)
			{
				url = null;
				return null;
			}
			url = "https://github.com/" + match.Groups["url"].Value;
			return match.Groups["version"].Value;
		}

		/// <summary>
		/// Called when enabling the window.
		/// </summary>
		private void OnEnable()
		{
			Refresh(false);
		}

		private void Refresh(bool forceFullRefresh)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			try
			{
				// reload the NuGet.config file, in case it was changed after Unity opened, but before the manager window opened (now)
				NugetHelper.LoadNugetConfigFile();

				// if we are entering playmode, don't do anything
				if (EditorApplication.isPlayingOrWillChangePlaymode)
				{
					return;
				}

				NugetHelper.LogVerbose(hasRefreshed ? "NugetWindow reloading config" : "NugetWindow reloading config and updating packages");

				// set the window title
				titleContent = new GUIContent("NuGet");

				if (!hasRefreshed || forceFullRefresh)
				{
					// reset the number to skip
					numberToSkip = 0;

					// TODO: Do we even need to load ALL of the data, or can we just get the Online tab packages?

					EditorUtility.DisplayProgressBar("Opening NuGet", "Fetching packages from server...", 0.3f);
					UpdateOnlinePackages();

					EditorUtility.DisplayProgressBar("Opening NuGet", "Getting installed packages...", 0.6f);
					NugetHelper.UpdateInstalledPackages();

					EditorUtility.DisplayProgressBar("Opening NuGet", "Getting available updates...", 0.9f);
					UpdateUpdatePackages();

					// load the default icon from the Resources folder
					defaultIcon = (Texture2D)Resources.Load("defaultIcon", typeof(Texture2D));
				}

				hasRefreshed = true;
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogErrorFormat("{0}", e.ToString());
			}
			finally
			{
				EditorUtility.ClearProgressBar();

				NugetHelper.LogVerbose("NugetWindow reloading took {0} ms", stopwatch.ElapsedMilliseconds);
			}
		}

		/// <summary>
		/// Updates the list of available packages by running a search with the server using the currently set parameters (# to get, # to skip, etc).
		/// </summary>
		private void UpdateOnlinePackages()
		{
			availablePackages = NugetHelper.Search(onlineSearchTerm != "Search" ? onlineSearchTerm : string.Empty, showAllOnlineVersions, showOnlinePrerelease, numberToGet, numberToSkip);
		}

		/// <summary>
		/// Updates the list of update packages.
		/// </summary>
		private void UpdateUpdatePackages()
		{
			// get any available updates for the installed packages
			updatePackages = NugetHelper.GetUpdates(NugetHelper.InstalledPackages, showPrereleaseUpdates, showAllUpdateVersions);
			filteredUpdatePackages = updatePackages;

			if (updatesSearchTerm != "Search")
			{
				filteredUpdatePackages = updatePackages.Where(x => x.Id.ToLower().Contains(updatesSearchTerm) || x.Title.ToLower().Contains(updatesSearchTerm)).ToList();
			}
		}

		/// <summary>
		/// From here: http://forum.unity3d.com/threads/changing-the-background-color-for-beginhorizontal.66015/
		/// </summary>
		/// <param name="width"></param>
		/// <param name="height"></param>
		/// <param name="col"></param>
		/// <returns></returns>
		private static Texture2D MakeTex(int width, int height, Color col)
		{
			var pix = new Color[width * height];

			for (var i = 0; i < pix.Length; i++)
				pix[i] = col;

			var result = new Texture2D(width, height);
			result.SetPixels(pix);
			result.Apply();

			return result;
		}

		/// <summary>
		/// Automatically called by Unity to draw the GUI.
		/// </summary>
		protected void OnGUI()
		{
			var selectedTab = GUILayout.Toolbar(currentTab, tabTitles);

			if (selectedTab != currentTab)
				OnTabChanged();

			currentTab = selectedTab;

			switch (currentTab)
			{
				case 0:
					DrawOnline();
					break;
				case 1:
					DrawInstalled();
					break;
				case 2:
					DrawUpdates();
					break;
			}
		}

		private void OnTabChanged()
		{
			openCloneWindows.Clear();
		}

		/// <summary>
		/// Creates a GUI style with a contrasting background color based upon if the Unity Editor is the free (light) skin or the Pro (dark) skin.
		/// </summary>
		/// <returns>A GUI style with the appropriate background color set.</returns>
		private static GUIStyle GetContrastStyle()
		{
			var style = new GUIStyle();
			var backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.6f, 0.6f, 0.6f);
			style.normal.background = MakeTex(16, 16, backgroundColor); 
			return style;
		}

		/// <summary>
		/// Creates a GUI style with a background color the same as the editor's current background color.
		/// </summary>
		/// <returns>A GUI style with the appropriate background color set.</returns>
		private static GUIStyle GetBackgroundStyle()
		{
			var style = new GUIStyle();
			var backgroundColor = EditorGUIUtility.isProSkin ? new Color32(56, 56, 56, 255) : new Color32(194, 194, 194, 255);
			style.normal.background = MakeTex(16, 16, backgroundColor); 
			return style;
		}

		/// <summary>
		/// Draws the list of installed packages that have updates available.
		/// </summary>
		private void DrawUpdates()
		{
			DrawUpdatesHeader();

			// display all of the installed packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			EditorGUILayout.BeginVertical();

			if (filteredUpdatePackages != null && filteredUpdatePackages.Count > 0)
			{
				DrawPackages(filteredUpdatePackages);
			}
			else
			{
				EditorStyles.label.fontStyle = FontStyle.Bold;
				EditorStyles.label.fontSize = 14;
				EditorGUILayout.LabelField("There are no updates available!", GUILayout.Height(20));
				EditorStyles.label.fontSize = 10;
				EditorStyles.label.fontStyle = FontStyle.Normal;
			}

			EditorGUILayout.EndVertical();
			EditorGUILayout.EndScrollView();
		}

		/// <summary>
		/// Draws the list of installed packages.
		/// </summary>
		private void DrawInstalled()
		{
			DrawInstalledHeader();

			// display all of the installed packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			EditorGUILayout.BeginVertical();

			var filteredInstalledPackages = FilteredInstalledPackages.ToList();
			if (filteredInstalledPackages.Count > 0)
			{
				DrawPackages(filteredInstalledPackages);
			}
			else
			{
				EditorStyles.label.fontStyle = FontStyle.Bold;
				EditorStyles.label.fontSize = 14;
				EditorGUILayout.LabelField("There are no packages installed!", GUILayout.Height(20));
				EditorStyles.label.fontSize = 10;
				EditorStyles.label.fontStyle = FontStyle.Normal;
			}

			EditorGUILayout.EndVertical();
			EditorGUILayout.EndScrollView();
		}

		/// <summary>
		/// Draws the current list of available online packages.
		/// </summary>
		private void DrawOnline()
		{
			DrawOnlineHeader();

			// display all of the packages
			scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
			EditorGUILayout.BeginVertical();

			DrawPackages(availablePackages);

			var showMoreStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				showMoreStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				showMoreStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			EditorGUILayout.BeginVertical(showMoreStyle);
			// allow the user to dislay more results
			if (GUILayout.Button("Show More", GUILayout.Width(120)))
			{
				numberToSkip += numberToGet;
				availablePackages.AddRange(NugetHelper.Search(onlineSearchTerm != "Search" ? onlineSearchTerm : string.Empty, showAllOnlineVersions, showOnlinePrerelease, numberToGet, numberToSkip));
			}
			EditorGUILayout.EndVertical();

			EditorGUILayout.EndVertical();
			EditorGUILayout.EndScrollView();
		}

		private void DrawPackages(List<NugetPackage> packages)
		{
			var backgroundStyle = GetBackgroundStyle();
			var contrastStyle = GetContrastStyle();

			for (var i = 0; i < packages.Count; i++)
			{
				EditorGUILayout.BeginVertical(backgroundStyle);
				DrawPackage(packages[i], backgroundStyle, contrastStyle);
				EditorGUILayout.EndVertical();

				// swap styles
				var tempStyle = backgroundStyle;
				backgroundStyle = contrastStyle;
				contrastStyle = tempStyle;
			}
		}

		/// <summary>
		/// Draws the header which allows filtering the online list of packages.
		/// </summary>
		private void DrawOnlineHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			EditorGUILayout.BeginVertical(headerStyle);
			{
				EditorGUILayout.BeginHorizontal();
				{
					var showAllVersionsTemp = EditorGUILayout.Toggle("Show All Versions", showAllOnlineVersions);
					if (showAllVersionsTemp != showAllOnlineVersions)
					{
						showAllOnlineVersions = showAllVersionsTemp;
						UpdateOnlinePackages();
					}

					if (GUILayout.Button("Refresh", GUILayout.Width(60)))
					{
						Refresh(true);
					}
				}
				EditorGUILayout.EndHorizontal();

				var showPrereleaseTemp = EditorGUILayout.Toggle("Show Prerelease", showOnlinePrerelease);
				if (showPrereleaseTemp != showOnlinePrerelease)
				{
					showOnlinePrerelease = showPrereleaseTemp;
					UpdateOnlinePackages();
				}

				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				EditorGUILayout.BeginHorizontal();
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					onlineSearchTerm = EditorGUILayout.TextField(onlineSearchTerm, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}
				EditorGUILayout.EndHorizontal();

				// search only if the enter key is pressed
				if (enterPressed)
				{
					// reset the number to skip
					numberToSkip = 0;
					UpdateOnlinePackages();
				}
			}
			EditorGUILayout.EndVertical();
		}

		/// <summary>
		/// Draws the header which allows filtering the installed list of packages.
		/// </summary>
		private void DrawInstalledHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			EditorGUILayout.BeginVertical(headerStyle);
			{
				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				EditorGUILayout.BeginHorizontal();
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					installedSearchTermEditBox = EditorGUILayout.TextField(installedSearchTermEditBox, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}
				EditorGUILayout.EndHorizontal();

				// search only if the enter key is pressed
				if (enterPressed)
				{
					installedSearchTerm = installedSearchTermEditBox;
				}
			}
			EditorGUILayout.EndVertical();
		}

		/// <summary>
		/// Draws the header for the Updates tab.
		/// </summary>
		private void DrawUpdatesHeader()
		{
			var headerStyle = new GUIStyle();
			if (Application.HasProLicense())
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.05f, 0.05f, 0.05f));
			}
			else
			{
				headerStyle.normal.background = MakeTex(20, 20, new Color(0.4f, 0.4f, 0.4f));
			}

			EditorGUILayout.BeginVertical(headerStyle);
			{
				EditorGUILayout.BeginHorizontal();
				{
					var showAllVersionsTemp = EditorGUILayout.Toggle("Show All Versions", showAllUpdateVersions);
					if (showAllVersionsTemp != showAllUpdateVersions)
					{
						showAllUpdateVersions = showAllVersionsTemp;
						UpdateUpdatePackages();
					}

					if (GUILayout.Button("Install All Updates", GUILayout.Width(150)))
					{
						NugetHelper.UpdateAll(updatePackages, NugetHelper.InstalledPackages);
						NugetHelper.UpdateInstalledPackages();
						UpdateUpdatePackages();
					}
				}
				EditorGUILayout.EndHorizontal();

				var showPrereleaseTemp = EditorGUILayout.Toggle("Show Prerelease", showPrereleaseUpdates);
				if (showPrereleaseTemp != showPrereleaseUpdates)
				{
					showPrereleaseUpdates = showPrereleaseTemp;
					UpdateUpdatePackages();
				}

				var enterPressed = Event.current.Equals(Event.KeyboardEvent("return"));

				EditorGUILayout.BeginHorizontal();
				{
					var oldFontSize = GUI.skin.textField.fontSize;
					GUI.skin.textField.fontSize = 25;
					updatesSearchTerm = EditorGUILayout.TextField(updatesSearchTerm, GUILayout.Height(30));

					if (GUILayout.Button("Search", GUILayout.Width(100), GUILayout.Height(28)))
					{
						// the search button emulates the Enter key
						enterPressed = true;
					}

					GUI.skin.textField.fontSize = oldFontSize;
				}
				EditorGUILayout.EndHorizontal();

				// search only if the enter key is pressed
				if (enterPressed)
				{
					if (updatesSearchTerm != "Search")
					{
						filteredUpdatePackages = updatePackages.Where(x => x.Id.ToLower().Contains(updatesSearchTerm) || x.Title.ToLower().Contains(updatesSearchTerm)).ToList();
					}
				}
			}
			EditorGUILayout.EndVertical();
		}

		/// <summary>
		/// Draws the given <see cref="NugetPackage"/>.
		/// </summary>
		/// <param name="package">The <see cref="NugetPackage"/> to draw.</param>
		private void DrawPackage(NugetPackage package, GUIStyle packageStyle, GUIStyle contrastStyle)
		{
			var installedPackages = NugetHelper.InstalledPackages;
			var installed = installedPackages.FirstOrDefault(p => p.Id == package.Id);

			EditorGUILayout.BeginHorizontal();
			{
				// The Unity GUI system (in the Editor) is terrible.  This probably requires some explanation.
				// Every time you use a Horizontal block, Unity appears to divide the space evenly.
				// (i.e. 2 components have half of the window width, 3 components have a third of the window width, etc)
				// GUILayoutUtility.GetRect is SUPPOSED to return a rect with the given height and width, but in the GUI layout.  It doesn't.
				// We have to use GUILayoutUtility to get SOME rect properties, but then manually calculate others.
				EditorGUILayout.BeginHorizontal();
				{
					const int iconSize = 32;
					const int leftPadding = 5;
					var rect = GUILayoutUtility.GetRect(iconSize, iconSize);
					// only use GetRect's Y position.  It doesn't correctly set the width or X position.
					rect.x = leftPadding;
					rect.y += 3;
					rect.width = iconSize;
					rect.height = iconSize;

					if (package.Icon != null)
					{
						GUI.DrawTexture(rect, package.Icon, ScaleMode.StretchToFill);
					}
					else
					{
						GUI.DrawTexture(rect, defaultIcon, ScaleMode.StretchToFill);
					}

					rect = GUILayoutUtility.GetRect(position.width / 2 - (iconSize + leftPadding), 20);
					rect.x = iconSize + leftPadding;
					rect.y += 10;

					EditorStyles.label.fontStyle = FontStyle.Bold;
					EditorStyles.label.fontSize = 14;
					////EditorGUILayout.LabelField(string.Format("{1} [{0}]", package.Version, package.Id), GUILayout.Height(20), GUILayout.Width(position.width / 2 - 32));
					GUI.Label(rect, string.Format("{1} [{0}]", package.Version, package.Title), EditorStyles.label);
					EditorStyles.label.fontSize = 10;
					EditorStyles.label.fontStyle = FontStyle.Normal;
				}
				EditorGUILayout.EndHorizontal();

				if (installedPackages.Contains(package))
				{
					// This specific version is installed
					if (GUILayout.Button("Uninstall", installButtonWidth, installButtonHeight))
					{
						NugetHelper.Uninstall(package, true, true);
						NugetHelper.UpdateInstalledPackages();
						UpdateUpdatePackages();
					}
				}
				else
				{
					if (installed != null)
					{
						if (installed < package)
						{
							// An older version is installed
							if (GUILayout.Button($"Update to [{package.Version}]", installButtonWidth, installButtonHeight))
							{
								NugetHelper.Update(installed, package);
								NugetHelper.UpdateInstalledPackages();
								UpdateUpdatePackages();
							}
						}
						else if (installed > package)
						{
							// A newer version is installed
							if (GUILayout.Button($"Downgrade to [{package.Version}]", installButtonWidth, installButtonHeight))
							{
								NugetHelper.Update(installed, package);
								NugetHelper.UpdateInstalledPackages();
								UpdateUpdatePackages();
							}
						}
					}
					else
					{
						if (GUILayout.Button("Install", installButtonWidth, installButtonHeight))
						{
							package.IsManuallyInstalled = true;
							NugetHelper.InstallIdentifier(package);
							AssetDatabase.Refresh();
							NugetHelper.UpdateInstalledPackages();
							UpdateUpdatePackages();
						}
					}
				}
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.BeginHorizontal();
			{
				EditorGUILayout.BeginVertical();
				{
					// Show the package description
					EditorStyles.label.wordWrap = true;
					EditorStyles.label.fontStyle = FontStyle.Normal;
					EditorGUILayout.LabelField($"{package.Description}");

					// Show project URL link
					if (!string.IsNullOrEmpty(package.ProjectUrl))
					{
						GUILayoutLink(package.ProjectUrl);
						GUILayout.Space(4f);
					}

					// Show the package release notes
					if (!string.IsNullOrEmpty(package.ReleaseNotes))
					{
						EditorStyles.label.wordWrap = true;
						EditorStyles.label.fontStyle = FontStyle.Bold;
						EditorGUILayout.LabelField("Release Notes:");
						EditorStyles.label.fontStyle = FontStyle.Normal;
						EditorGUILayout.LabelField($"{package.ReleaseNotes}");
					}

					// Show the dependencies
					if (package.Dependencies.Count > 0)
					{
						EditorStyles.label.wordWrap = true;
						EditorStyles.label.fontStyle = FontStyle.Italic;
						var builder = new StringBuilder();
						foreach (var dependency in package.Dependencies)
						{
							builder.Append($" {dependency.Id} {dependency.Version};");
						}
						EditorGUILayout.Space();
						EditorGUILayout.LabelField($"Depends on:{builder}");
						EditorStyles.label.fontStyle = FontStyle.Normal;
					}

					// Create the style for putting a box around the 'Clone' button
					var cloneButtonBoxStyle = new GUIStyle("box")
					{
						stretchWidth = false,
						margin =
						{
							top = 0,
							bottom = 0
						},
						padding = {bottom = 4}
					};

					var normalButtonBoxStyle = new GUIStyle(cloneButtonBoxStyle) {normal = {background = packageStyle.normal.background}};

					var showCloneWindow = openCloneWindows.Contains(package);
					cloneButtonBoxStyle.normal.background = showCloneWindow ? contrastStyle.normal.background : packageStyle.normal.background;

					// Create a simillar style for the 'Clone' window
					var cloneWindowStyle = new GUIStyle(cloneButtonBoxStyle) {padding = new RectOffset(6, 6, 2, 6)};

					// Show button bar
					EditorGUILayout.BeginHorizontal();
					{
						if (package.RepositoryType == RepositoryType.Git || package.RepositoryType == RepositoryType.TfsGit)
						{
							if (!string.IsNullOrEmpty(package.RepositoryUrl))
							{
								EditorGUILayout.BeginHorizontal(cloneButtonBoxStyle);
								{
									var cloneButtonStyle = new GUIStyle(GUI.skin.button);
									cloneButtonStyle.normal = showCloneWindow ? cloneButtonStyle.active : cloneButtonStyle.normal;
									if (GUILayout.Button("Clone", cloneButtonStyle, GUILayout.ExpandWidth(false)))
									{
										showCloneWindow = !showCloneWindow;
									}

									if (showCloneWindow)
										openCloneWindows.Add(package);
									else
										openCloneWindows.Remove(package);
								}
								EditorGUILayout.EndHorizontal();
							}
						}

						if (!string.IsNullOrEmpty(package.LicenseUrl) && package.LicenseUrl != "http://your_license_url_here")
						{
							// Creaete a box around the license button to keep it alligned with Clone button
							EditorGUILayout.BeginHorizontal(normalButtonBoxStyle);
							// Show the license button
							if (GUILayout.Button("View License", GUILayout.ExpandWidth(false)))
							{
								Application.OpenURL(package.LicenseUrl);
							}
							EditorGUILayout.EndHorizontal();
						}
					}
					EditorGUILayout.EndHorizontal();

					if (showCloneWindow)
					{
						EditorGUILayout.BeginVertical(cloneWindowStyle);
						{
							// Clone latest label
							EditorGUILayout.BeginHorizontal();
							GUILayout.Space(20f);
							EditorGUILayout.LabelField("clone latest");
							EditorGUILayout.EndHorizontal();

							// Clone latest row
							EditorGUILayout.BeginHorizontal();
							{
								if (GUILayout.Button("Copy", GUILayout.ExpandWidth(false)))
								{
									GUI.FocusControl(package.Id + package.Version + "repoUrl");
									GUIUtility.systemCopyBuffer = package.RepositoryUrl;
								}

								GUI.SetNextControlName(package.Id + package.Version + "repoUrl");
								EditorGUILayout.TextField(package.RepositoryUrl);
							}
							EditorGUILayout.EndHorizontal();

							// Clone @ commit label
							GUILayout.Space(4f);
							EditorGUILayout.BeginHorizontal();
							GUILayout.Space(20f);
							EditorGUILayout.LabelField("clone @ commit");
							EditorGUILayout.EndHorizontal();

							// Clone @ commit row
							EditorGUILayout.BeginHorizontal();
							{
								// Create the three commands a user will need to run to get the repo @ the commit. Intentionally leave off the last newline for better UI appearance
								var commands = string.Format("git clone {0} {1} --no-checkout{2}cd {1}{2}git checkout {3}",  package.RepositoryUrl, package.Id, Environment.NewLine, package.RepositoryCommit);

								if (GUILayout.Button("Copy", GUILayout.ExpandWidth(false)))
								{
									GUI.FocusControl(package.Id + package.Version + "commands");

									// Add a newline so the last command will execute when pasted to the CL
									GUIUtility.systemCopyBuffer = (commands + Environment.NewLine);
								}

								EditorGUILayout.BeginVertical();
								GUI.SetNextControlName(package.Id + package.Version + "commands");
								EditorGUILayout.TextArea(commands);
								EditorGUILayout.EndVertical();
							}
							EditorGUILayout.EndHorizontal();
						}
						EditorGUILayout.EndVertical();
					}

					EditorGUILayout.Separator();
					EditorGUILayout.Separator();
				}
				EditorGUILayout.EndVertical();

				if (installed != null)
				{
					var labelStyle = new GUIStyle(EditorStyles.label) {alignment = TextAnchor.UpperRight};
					GUILayout.Label($"currently [{installed.Version}]  ", labelStyle, installButtonWidth);
				}
			}
			EditorGUILayout.EndHorizontal();
		}

		public static void GUILayoutLink(string url)
		{
			var hyperLinkStyle = new GUIStyle(GUI.skin.label)
			{
				stretchWidth = false,
				richText = true
			};

			var colorFormatString = "<color=#add8e6ff>{0}</color>";

			var underline = new string('_', url.Length);

			var formattedUrl = string.Format(colorFormatString, url);
			var formattedUnderline = string.Format(colorFormatString, underline);
			var urlRect = GUILayoutUtility.GetRect(new GUIContent(url), hyperLinkStyle);
			GUI.Label(urlRect, formattedUrl, hyperLinkStyle);
			GUI.Label(urlRect, formattedUnderline, hyperLinkStyle);

			EditorGUIUtility.AddCursorRect(urlRect, MouseCursor.Link);
			if (urlRect.Contains(Event.current.mousePosition))
			{
				if (Event.current.type == EventType.MouseUp)
					Application.OpenURL(url);
			}
		}
	}
}