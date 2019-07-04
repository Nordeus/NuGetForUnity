﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;

namespace NugetForUnity
{
	/// <summary>
	/// Represents a NuGet Package Source (a "server").
	/// </summary>
	public class NugetPackageSource
	{
		/// <summary>
		/// Gets or sets the name of the package source.
		/// </summary>
		public string Name { get; set; }

		/// <summary>
		/// Gets or sets the path of the package source.
		/// </summary>
		public string SavedPath { get; set; }

		/// <summary>
		/// Gets path, with the values of environment variables expanded.
		/// </summary>
		public string ExpandedPath => Environment.ExpandEnvironmentVariables(SavedPath);

		public string UserName { get; set; }

		/// <summary>
		/// Gets or sets the password used to access the feed. Null indicates that no password is used.
		/// </summary>
		public string SavedPassword { get; set; }

		/// <summary>
		/// Gets password, with the values of environment variables expanded.
		/// </summary>
		public string ExpandedPassword => SavedPassword != null ? Environment.ExpandEnvironmentVariables(SavedPassword) : null;

		public bool HasPassword
		{
			get => SavedPassword != null;

			set
			{
				if (value)
				{
					if (SavedPassword == null)
					{
						SavedPassword = string.Empty; // Initialize newly-enabled password to empty string.
					}
				}
				else
				{
					SavedPassword = null; // Clear password to null when disabled.
				}
			}
		}

		/// <summary>
		/// Gets or sets a value indicated whether the path is a local path or a remote path.
		/// </summary>
		public bool IsLocalPath { get; }

		/// <summary>
		/// Gets or sets a value indicated whether this source is enabled or not.
		/// </summary>
		public bool IsEnabled { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="NugetPackageSource"/> class.
		/// </summary>
		/// <param name="name">The name of the package source.</param>
		/// <param name="path">The path to the package source.</param>
		public NugetPackageSource(string name, string path)
		{
			Name = name;
			SavedPath = path;
			IsLocalPath = !ExpandedPath.StartsWith("http");
			IsEnabled = true;
		}

		/// <summary>
		/// Gets a NugetPackage from the NuGet server that matches (or is in range of) the <see cref="NugetPackageIdentifier"/> given.
		/// If an exact match isn't found, it selects the next closest version available.
		/// </summary>
		/// <param name="package">The <see cref="NugetPackageIdentifier"/> containing the ID and Version of the package to get.</param>
		/// <returns>The retrieved package, if there is one.  Null if no matching package was found.</returns>
		public List<NugetPackage> FindPackagesById(NugetPackageIdentifier package)
		{
			List<NugetPackage> foundPackages;

			if (IsLocalPath)
			{
				var localPackagePath = Path.Combine(ExpandedPath, $"./{package.Id}.{package.Version}.nupkg");
				if (File.Exists(localPackagePath))
				{
					var localPackage = NugetPackage.FromNupkgFile(localPackagePath);
					foundPackages = new List<NugetPackage> { localPackage };
				}
				else
				{
					// TODO: Sort the local packages?  Currently assuming they are in alphabetical order due to the filesystem.
					// TODO: Optimize to no longer use GetLocalPackages, since that loads the .nupkg itself

					// Try to find later versions of the same package
					var packages = GetLocalPackages(package.Id, true, true);
					foundPackages = new List<NugetPackage>(packages.SkipWhile(x => !package.InRange(x)));
				  }
			}
			else
			{
				// See here: http://www.odata.org/documentation/odata-version-2-0/uri-conventions/

				// We used to rely on expressions such as &$filter=Version ge '9.0.1' to find versions in a range, but the results were sorted alphabetically. This
				// caused version 10.0.0 to be less than version 9.0.0. In order to work around this issue, we'll request all versions and perform filtering ourselves.

				var url = $"{ExpandedPath}FindPackagesById()?$orderby=Version asc&id='{package.Id}'";

				try
				{
					foundPackages = GetPackagesFromUrl(url, UserName, ExpandedPassword);
				}
				catch (Exception e)
				{
					foundPackages = new List<NugetPackage>();
					SystemProxy.LogError($"Unable to retrieve package list from {url}\n{e}");
				}

				foundPackages.Sort();
				if (foundPackages.Exists(package.InRange))
				{
					// Return all the packages in the range of versions specified by 'package'.
					foundPackages.RemoveAll(p => !package.InRange(p));
				}
				else
				{
					// There are no packages in the range of versions specified by 'package'.
					// Return the most recent version after the version specified by 'package'.
					foundPackages.RemoveAll(p => package.CompareVersion(p.Version) < 0);
					if (foundPackages.Count > 0)
					{
						foundPackages.RemoveRange(1, foundPackages.Count - 1);
					}
				}
			}

			foreach (var foundPackage in foundPackages)
			{
				foundPackage.PackageSource = this;
			}

			return foundPackages;
		}

		/// <summary>
		/// Gets a NugetPackage from the NuGet server that matches (or is in range of) the <see cref="NugetPackageIdentifier"/> given.
		/// If an exact match isn't found, it selects the next closest version available.
		/// </summary>
		/// <param name="package">The <see cref="NugetPackageIdentifier"/> containing the ID and Version of the package to get.</param>
		/// <returns>The retrieved package, if there is one.  Null if no matching package was found.</returns>
		public NugetPackage GetSpecificPackage(NugetPackageIdentifier package)
		{
			return FindPackagesById(package).LastOrDefault();
		}

		/// <summary>
		/// Gets a list of NuGetPackages from this package source.
		/// This allows searching for partial IDs or even the empty string (the default) to list ALL packages.
		/// 
		/// NOTE: See the functions and parameters defined here: https://www.nuget.org/api/v2/$metadata
		/// </summary>
		/// <param name="searchTerm">The search term to use to filter packages. Defaults to the empty string.</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="numberToGet">The number of packages to fetch.</param>
		/// <param name="numberToSkip">The number of packages to skip before fetching.</param>
		/// <returns>The list of available packages.</returns>
		public List<NugetPackage> Search(string searchTerm = "", bool includeAllVersions = false, bool includePrerelease = false, int numberToGet = 15, int numberToSkip = 0)
		{
			if (IsLocalPath)
			{
				return GetLocalPackages(searchTerm, includeAllVersions, includePrerelease, numberToSkip);
			}

			//Example URL: "http://www.nuget.org/api/v2/Search()?$filter=IsLatestVersion&$orderby=Id&$skip=0&$top=30&searchTerm='newtonsoft'&targetFramework=''&includePrerelease=false";

			var url = ExpandedPath;

			// call the search method
			url += "Search()?";

			// filter results
			if (!includeAllVersions)
			{
				if (!includePrerelease)
				{
					url += "$filter=IsLatestVersion&";
				}
				else
				{
					url += "$filter=IsAbsoluteLatestVersion&";
				}
			}

			// order results
			//url += "$orderby=Id&";
			//url += "$orderby=LastUpdated&";
			url += "$orderby=DownloadCount desc&";

			// skip a certain number of entries
			url += $"$skip={numberToSkip}&";

			// show a certain number of entries
			url += $"$top={numberToGet}&";

			// apply the search term
			url += $"searchTerm='{searchTerm}'&";

			// apply the target framework filters
			url += "targetFramework=''&";

			// should we include prerelease packages?
			url += $"includePrerelease={includePrerelease.ToString().ToLower()}";

			try
			{
				return GetPackagesFromUrl(url, UserName, ExpandedPassword);
			}
			catch (Exception e)
			{
				SystemProxy.LogError($"Unable to retrieve package list from {url}\n{e}");
				return new List<NugetPackage>();
			}
		}

		/// <summary>
		/// Gets a list of all available packages from a local source (not a web server) that match the given filters.
		/// </summary>
		/// <param name="searchTerm">The search term to use to filter packages. Defaults to the empty string.</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="numberToSkip">The number of packages to skip before fetching.</param>
		/// <returns>The list of available packages.</returns>
		private List<NugetPackage> GetLocalPackages(string searchTerm = "", bool includeAllVersions = false, bool includePrerelease = false, int numberToSkip = 0)
		{
			var localPackages = new List<NugetPackage>();

			if (numberToSkip != 0)
			{
				// we return the entire list the first time, so no more to add
				return localPackages;
			}

			var path = ExpandedPath;

			if (Directory.Exists(path))
			{
				var packagePaths = Directory.GetFiles(path, $"*{searchTerm}*.nupkg");

				foreach (var packagePath in packagePaths)
				{
					var package = NugetPackage.FromNupkgFile(packagePath);
					package.PackageSource = this;

					if (package.IsPrerelease && !includePrerelease)
					{
						// if it's a prerelease package and we aren't supposed to return prerelease packages, just skip it
						continue;
					}

					if (includeAllVersions)
					{
						// if all versions are being included, simply add it and move on
						localPackages.Add(package);
						//LogVerbose("Adding {0} {1}", package.Id, package.Version);
						continue;
					}

					var existingPackage = localPackages.FirstOrDefault(x => x.Id == package.Id);
					if (existingPackage != null)
					{
						// there is already a package with the same ID in the list
						if (existingPackage < package)
						{
							// if the current package is newer than the existing package, swap them
							localPackages.Remove(existingPackage);
							localPackages.Add(package);
						}
					}
					else
					{
						// there is no package with the same ID in the list yet
						localPackages.Add(package);
					}
				}
			}
			else
			{
				SystemProxy.LogError($"Local folder not found: {path}");
			}

			return localPackages;
		}

		/// <summary>
		/// Builds a list of NugetPackages from the XML returned from the HTTP GET request issued at the given URL.
		/// Note that NuGet uses an Atom-feed (XML Syndicaton) superset called OData.
		/// See here http://www.odata.org/documentation/odata-version-2-0/uri-conventions/
		/// </summary>
		/// <param name="url"></param>
		/// <param name="password"></param>
		/// <returns></returns>
		private List<NugetPackage> GetPackagesFromUrl(string url, string username, string password)
		{
			NugetHelper.LogVerbose("Getting packages from: {0}", url);

			var stopwatch = new Stopwatch();
			stopwatch.Start();

			// Mono doesn't have a Certificate Authority, so we have to provide all validation manually.  Currently just accept anything.
			// See here: http://stackoverflow.com/questions/4926676/mono-webrequest-fails-with-https

			// remove all handlers
			ServicePointManager.ServerCertificateValidationCallback = null;

			// add anonymous handler
			ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, policyErrors) => true;

			var responseStream = NugetHelper.RequestUrl(url, username, password, timeOut: 5000);
			var streamReader = new StreamReader(responseStream);

			var packages = NugetODataResponse.Parse(XDocument.Load(streamReader));

			foreach (var package in packages)
			{
				package.PackageSource = this;
			}

			stopwatch.Stop();
			NugetHelper.LogVerbose("Retreived {0} packages in {1} ms", packages.Count, stopwatch.ElapsedMilliseconds);

			return packages;
		}

		/// <summary>
		/// Gets a list of available packages from a local source (not a web server) that are upgrades for the given list of installed packages.
		/// </summary>
		/// <param name="installedPackages">The list of currently installed packages to use to find updates.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <returns>A list of all updates available.</returns>
		private List<NugetPackage> GetLocalUpdates(IEnumerable<NugetPackage> installedPackages, bool includePrerelease = false, bool includeAllVersions = false)
		{
			var updates = new List<NugetPackage>();

			var availablePackages = GetLocalPackages(string.Empty, includeAllVersions, includePrerelease);
			foreach (var installedPackage in installedPackages)
			{
				foreach (var availablePackage in availablePackages)
				{
					if (installedPackage.Id == availablePackage.Id)
					{
						if (installedPackage < availablePackage)
						{
							updates.Add(availablePackage);
						}
					}
				}
			}

			return updates;
		}

		/// <summary>
		/// Queries the source with the given list of installed packages to get any updates that are available.
		/// </summary>
		/// <param name="installedPackages">The list of currently installed packages.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="targetFrameworks">The specific frameworks to target?</param>
		/// <param name="versionContraints">The version constraints?</param>
		/// <returns>A list of all updates available.</returns>
		public List<NugetPackage> GetUpdates(ICollection<NugetPackage> installedPackages, bool includePrerelease = false, bool includeAllVersions = false, string targetFrameworks = "", string versionContraints = "")
		{
			if (IsLocalPath)
			{
				return GetLocalUpdates(installedPackages, includePrerelease, includeAllVersions);
			}

			var updates = new List<NugetPackage>();

			// check for updates in groups of 10 instead of all of them, since that causes servers to throw errors for queries that are too long
			for (var i = 0; i < installedPackages.Count; i += 10)
			{
				var packageGroup = installedPackages.Skip(i).Take(10);

				var packageIds = string.Empty;
				var versions = string.Empty;

				foreach (var package in packageGroup)
				{
					if (string.IsNullOrEmpty(packageIds))
					{
						packageIds += package.Id;
					}
					else
					{
						packageIds += "|" + package.Id;
					}

					if (string.IsNullOrEmpty(versions))
					{
						versions += package.Version;
					}
					else
					{
						versions += "|" + package.Version;
					}
				}

				var url =
					$"{ExpandedPath}GetUpdates()?packageIds='{packageIds}'&versions='{versions}'&includePrerelease={includePrerelease.ToString().ToLower()}&includeAllVersions={includeAllVersions.ToString().ToLower()}&targetFrameworks='{targetFrameworks}'&versionConstraints='{versionContraints}'";

				try
				{
					var newPackages = GetPackagesFromUrl(url, UserName, ExpandedPassword);
					updates.AddRange(newPackages);
				}
				catch (Exception e)
				{
					var webResponse = e is WebException webException ? webException.Response as HttpWebResponse : null;
					if (webResponse != null && webResponse.StatusCode == HttpStatusCode.NotFound)
					{
						// Some web services, such as VSTS don't support the GetUpdates API. Attempt to retrieve updates via FindPackagesById.
						NugetHelper.LogVerbose("{0} not found. Falling back to FindPackagesById.", url);
						return GetUpdatesFallback(installedPackages, includePrerelease, includeAllVersions, targetFrameworks, versionContraints);
					}

					SystemProxy.LogError($"Unable to retrieve package list from {url}\n{e}");
				}
			}

			// sort alphabetically
			updates.Sort(delegate (NugetPackage x, NugetPackage y)
			{
				if (x.Id == null && y.Id == null) return 0;
				if (x.Id == null) return -1;
				if (y.Id == null) return 1;
				if (x.Id == y.Id) return string.Compare(x.Version, y.Version, StringComparison.Ordinal);
					return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
			});

#if TEST_GET_UPDATES_FALLBACK
			// Enable this define in order to test that GetUpdatesFallback is working as intended. This tests that it returns the same set of packages
			// that are returned by the GetUpdates API. Since GetUpdates isn't available when using a Visual Studio Team Services feed, the intention
			// is that this test would be conducted by using nuget.org's feed where both paths can be compared.
			List<NugetPackage> updatesReplacement = GetUpdatesFallback(installedPackages, includePrerelease, includeAllVersions, targetFrameworks, versionContraints);
			ComparePackageLists(updates, updatesReplacement, "GetUpdatesFallback doesn't match GetUpdates API");
#endif

			return updates;
		}

#if TEST_GET_UPDATES_FALLBACK
		private static void ComparePackageLists(List<NugetPackage> updates, List<NugetPackage> updatesReplacement, string errorMessageToDisplayIfListsDoNotMatch)
		{
			var matchingComparison = new System.Text.StringBuilder();
			var missingComparison = new System.Text.StringBuilder();
			foreach (var package in updates)
			{
				if (updatesReplacement.Contains(package))
				{
					matchingComparison.Append(matchingComparison.Length == 0 ? "Matching: " : ", ");
					matchingComparison.Append(package.ToString());
				}
				else
				{
					missingComparison.Append(missingComparison.Length == 0 ? "Missing: " : ", ");
					missingComparison.Append(package.ToString());
				}
			}
			var extraComparison = new System.Text.StringBuilder();
			foreach (var package in updatesReplacement)
			{
				if (!updates.Contains(package))
				{
					extraComparison.Append(extraComparison.Length == 0 ? "Extra: " : ", ");
					extraComparison.Append(package.ToString());
				}
			}
			if (missingComparison.Length > 0 || extraComparison.Length > 0)
			{
				Debug.LogWarningFormat("{0}\n{1}\n{2}\n{3}", errorMessageToDisplayIfListsDoNotMatch, matchingComparison, missingComparison, extraComparison);
			}
		}
#endif

		/// <summary>
		/// Some NuGet feeds such as Visual Studio Team Services do not implement the GetUpdates function.
		/// In that case this fallback function can be used to retrieve updates by using the FindPackagesById function.
		/// </summary>
		/// <param name="installedPackages">The list of currently installed packages.</param>
		/// <param name="includePrerelease">True to include prerelease packages (alpha, beta, etc).</param>
		/// <param name="includeAllVersions">True to include older versions that are not the latest version.</param>
		/// <param name="targetFrameworks">The specific frameworks to target?</param>
		/// <param name="versionContraints">The version constraints?</param>
		/// <returns>A list of all updates available.</returns>
		private List<NugetPackage> GetUpdatesFallback(IEnumerable<NugetPackage> installedPackages, bool includePrerelease = false, bool includeAllVersions = false, string targetFrameworks = "", string versionContraints = "")
		{
			Debug.Assert(string.IsNullOrEmpty(targetFrameworks) && string.IsNullOrEmpty(versionContraints)); // These features are not supported by this version of GetUpdates.

			var updates = new List<NugetPackage>();
			foreach (var installedPackage in installedPackages)
			{
				var versionRange = $"({installedPackage.Version},)"; // Minimum of Current ID (exclusive) with no maximum (exclusive).
				var id = new NugetPackageIdentifier(installedPackage.Id, versionRange); 
				var packageUpdates = FindPackagesById(id);

				var mostRecentPrerelease = includePrerelease ? packageUpdates.FindLast(p => p.IsPrerelease) : default(NugetPackage);
				packageUpdates.RemoveAll(p => p.IsPrerelease && p != mostRecentPrerelease);

				if (!includeAllVersions && packageUpdates.Count > 0)
				{
					packageUpdates.RemoveRange(0, packageUpdates.Count - 1);
				}

				updates.AddRange(packageUpdates);
			}

			return updates;
		}
	}
}