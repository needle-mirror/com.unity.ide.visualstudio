/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using IOPath = System.IO.Path;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Unity.VisualStudio.Editor
{
	internal class VisualStudioCodeInstallation : VisualStudioInstallation
	{
		private static readonly IGenerator _generator = new SdkStyleProjectGeneration();

		public override bool SupportsAnalyzers
		{
			get
			{
				return true;
			}
		}

		public override Version LatestLanguageVersionSupported
		{
			get
			{
				return new Version(11, 0);
			}
		}

		private string GetExtensionPath()
		{
			var vscode = IsPrerelease ? ".vscode-insiders" : ".vscode";
			var extensionsPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), vscode, "extensions");
			if (!Directory.Exists(extensionsPath))
				return null;

			return Directory
				.EnumerateDirectories(extensionsPath, "visualstudiotoolsforunity.vstuc*") // publisherid.extensionid
				.OrderByDescending(n => n)
				.FirstOrDefault();
		}

		public override string[] GetAnalyzers()
		{
			var vstuPath = GetExtensionPath();
			if (string.IsNullOrEmpty(vstuPath))
				return Array.Empty<string>();

			return GetAnalyzers(vstuPath); }

		public override IGenerator ProjectGenerator
		{
			get
			{
				return _generator;
			}
		}

		private static bool IsCandidateForDiscovery(string path)
		{
			if (VisualStudioEditor.IsOSX)
				return Directory.Exists(path) && Regex.IsMatch(path, ".*Code.*.app$", RegexOptions.IgnoreCase);

			if (VisualStudioEditor.IsWindows)
				return File.Exists(path) && Regex.IsMatch(path, ".*Code.*.exe$", RegexOptions.IgnoreCase);

			return File.Exists(path) && path.EndsWith("code", StringComparison.OrdinalIgnoreCase);
		}

		[Serializable]
		internal class VisualStudioCodeManifest
		{
			public string name;
			public string version;
		}

		public static bool TryDiscoverInstallation(string editorPath, out IVisualStudioInstallation installation)
		{
			installation = null;

			if (string.IsNullOrEmpty(editorPath))
				return false;

			if (!IsCandidateForDiscovery(editorPath))
				return false;

			Version version = null;
			var isPrerelease = false;

			try
			{
				var manifestBase = GetRealPath(editorPath);

				if (VisualStudioEditor.IsWindows)  // on Windows, editorPath is a file, resources as subdirectory
					manifestBase = IOPath.GetDirectoryName(manifestBase);
				else if (VisualStudioEditor.IsOSX) // on Mac, editorPath is a directory
					manifestBase = IOPath.Combine(manifestBase, "Contents");
				else                               // on Linux, editorPath is a file, in a bin sub-directory
					manifestBase = Directory.GetParent(manifestBase)?.Parent?.FullName;

				if (manifestBase == null)
					return false;

				var manifestFullPath = IOPath.Combine(manifestBase, @"resources", "app", "package.json");
				if (File.Exists(manifestFullPath))
				{
					var manifest = JsonUtility.FromJson<VisualStudioCodeManifest>(File.ReadAllText(manifestFullPath));
					Version.TryParse(manifest.version.Split('-').First(), out version);
					isPrerelease = manifest.version.ToLower().Contains("insider");
				}
			}
			catch (Exception)
			{
				// do not fail if we are not able to retrieve the exact version number
			}

			isPrerelease = isPrerelease || editorPath.ToLower().Contains("insider");
			installation = new VisualStudioCodeInstallation()
			{
				IsPrerelease = isPrerelease,
				Name = "Visual Studio Code" + (isPrerelease ? " - Insider" : string.Empty) + (version != null ? $" [{version.ToString(3)}]" : string.Empty),
				Path = editorPath,
				Version = version ?? new Version()
			};

			return true;
		}

		public static IEnumerable<IVisualStudioInstallation> GetVisualStudioInstallations()
		{
			var candidates = new List<string>();

			if (VisualStudioEditor.IsWindows)
			{
				var localAppPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
				var programFiles = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

				foreach (var basePath in new[] {localAppPath, programFiles})
				{
					candidates.Add(IOPath.Combine(basePath, "Microsoft VS Code", "Code.exe"));
					candidates.Add(IOPath.Combine(basePath, "Microsoft VS Code Insiders", "Code - Insiders.exe"));
				}
			}
			else if (VisualStudioEditor.IsOSX)
			{
				var appPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
				candidates.AddRange(Directory.EnumerateDirectories(appPath, "Visual Studio Code*.app"));
			}
			else
			{
				candidates.Add("/usr/bin/code");
				candidates.Add("/bin/code");
				candidates.Add("/usr/local/bin/code");
			}

			foreach (var candidate in candidates)
			{
				if (TryDiscoverInstallation(candidate, out var installation))
					yield return installation;
			}
		}

#if UNITY_EDITOR_LINUX
		[DllImport ("libc")]
		private static extern int readlink(string path, byte[] buffer, int buflen);

		internal static string GetRealPath(string path)
		{
			byte[] buf = new byte[512];
			int ret = readlink(path, buf, buf.Length);
			if (ret == -1) return path;
			char[] cbuf = new char[512];
			int chars = System.Text.Encoding.Default.GetChars(buf, 0, ret, cbuf, 0);
			return new String(cbuf, 0, chars);
		}
#else
		internal static string GetRealPath(string path)
		{
			return path;
		}
#endif

		public override void CreateExtraFiles(string projectDirectory)
		{
			try
			{
				// see https://tattoocoder.com/recommending-vscode-extensions-within-your-open-source-projects/
				var vscodeDirectory = IOPath.Combine(projectDirectory.NormalizePathSeparators(), ".vscode");
				Directory.CreateDirectory(vscodeDirectory);

				CreateRecommendedExtensionsFile(vscodeDirectory);
				CreateSettingsFile(vscodeDirectory);
				CreateLaunchFile(vscodeDirectory);
			}
			catch (IOException)
			{
			}			
		}

		private static void CreateLaunchFile(string vscodeDirectory)
		{
			var launchFile = IOPath.Combine(vscodeDirectory, "launch.json");
			if (File.Exists(launchFile))
				return;

			const string content = @"{
    ""version"": ""0.2.0"",
    ""configurations"": [
        {
            ""name"": ""Attach to Unity"",
            ""type"": ""vstuc"",
            ""request"": ""attach"",
        }
     ]
}";

			File.WriteAllText(launchFile, content);
		}

		private static void CreateSettingsFile(string vscodeDirectory)
		{
			var settingsFile = IOPath.Combine(vscodeDirectory, "settings.json");
			if (File.Exists(settingsFile))
				return;

			const string content = @"{
    ""files.exclude"":
    {
        ""**/.DS_Store"":true,
        ""**/.git"":true,
        ""**/.vs"":true,
        ""**/.gitmodules"":true,
        ""**/.vsconfig"":true,
        ""**/*.booproj"":true,
        ""**/*.pidb"":true,
        ""**/*.suo"":true,
        ""**/*.user"":true,
        ""**/*.userprefs"":true,
        ""**/*.unityproj"":true,
        ""**/*.dll"":true,
        ""**/*.exe"":true,
        ""**/*.pdf"":true,
        ""**/*.mid"":true,
        ""**/*.midi"":true,
        ""**/*.wav"":true,
        ""**/*.gif"":true,
        ""**/*.ico"":true,
        ""**/*.jpg"":true,
        ""**/*.jpeg"":true,
        ""**/*.png"":true,
        ""**/*.psd"":true,
        ""**/*.tga"":true,
        ""**/*.tif"":true,
        ""**/*.tiff"":true,
        ""**/*.3ds"":true,
        ""**/*.3DS"":true,
        ""**/*.fbx"":true,
        ""**/*.FBX"":true,
        ""**/*.lxo"":true,
        ""**/*.LXO"":true,
        ""**/*.ma"":true,
        ""**/*.MA"":true,
        ""**/*.obj"":true,
        ""**/*.OBJ"":true,
        ""**/*.asset"":true,
        ""**/*.cubemap"":true,
        ""**/*.flare"":true,
        ""**/*.mat"":true,
        ""**/*.meta"":true,
        ""**/*.prefab"":true,
        ""**/*.unity"":true,
        ""build/"":true,
        ""Build/"":true,
        ""Library/"":true,
        ""library/"":true,
        ""obj/"":true,
        ""Obj/"":true,
        ""Logs/"":true,
        ""logs/"":true,
        ""ProjectSettings/"":true,
        ""UserSettings/"":true,
        ""temp/"":true,
        ""Temp/"":true
    },
    ""omnisharp.enableRoslynAnalyzers"": true
}";

			File.WriteAllText(settingsFile, content);
		}

		private static void CreateRecommendedExtensionsFile(string vscodeDirectory)
		{
			var extensionFile = IOPath.Combine(vscodeDirectory, "extensions.json");
			if (File.Exists(extensionFile))
				return;

			const string content = @"{
    ""recommendations"": [
      ""visualstudiotoolsforunity.vstuc""
    ]
}
";
			File.WriteAllText(extensionFile, content);
		}

		public override bool Open(string path, int line, int column, string solution)
		{
			line = Math.Max(1, line);
			column = Math.Max(0, column);

			var directory = IOPath.GetDirectoryName(solution);
			var application = Path;

			ProcessRunner.Start(string.IsNullOrEmpty(path) ? 
				ProcessStartInfoFor(application, $"\"{directory}\"") :
				ProcessStartInfoFor(application, $"\"{directory}\" -g \"{path}\":{line}:{column}"));

			return true;
		}

		private static ProcessStartInfo ProcessStartInfoFor(string application, string arguments)
		{
			if (!VisualStudioEditor.IsOSX)
				return ProcessRunner.ProcessStartInfoFor(application, arguments, redirect: false);

			// wrap with built-in OSX open feature
			arguments = $"-n \"{application}\" --args {arguments}";
			application = "open";
			return ProcessRunner.ProcessStartInfoFor(application, arguments, redirect:false, shell: true);
		}

		public static void Initialize()
		{
		}
	}
}
