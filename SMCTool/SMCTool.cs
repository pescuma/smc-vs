﻿// Based on work by Richard Lowe for CustomToolTemplate: http://customtooltemplate.codeplex.com/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using VSLangProj;
using Process = System.Diagnostics.Process;

namespace CustomToolTemplate
{
	[ComVisible(true)]
	[CustomToolRegistration("SMCTool", typeof (SMCTool), FileExtension = ".sm")]
	[ProvideObject(typeof (SMCTool))]
	public class SMCTool : CustomToolBase
	{
		private static string _installPath;
		protected readonly Regex CommandLineRe;
		protected readonly Regex DotCommandLineRe;
		protected readonly Regex ErrorRe;
		protected readonly Regex GraphCommandLineRe;
		protected readonly Regex InfoRe;

		public SMCTool()
		{
			InfoRe = new Regex(@"\[(?<info>.*)\]");
			ErrorRe = new Regex(@":(?<line>\d+): (?<level>[^ ]+) - (?<err>.*)");
			CommandLineRe = new Regex(@"^Command\s+Line\s*:(\s*(?<arg>[^ ]+))+$",
			                          RegexOptions.IgnoreCase);
			GraphCommandLineRe = new Regex(@"^Graph\s+Command\s+Line\s*:(\s*(?<arg>[^ ]+))+$",
			                               RegexOptions.IgnoreCase);
			DotCommandLineRe = new Regex(@"^dot\s+Command\s+Line\s*:(\s*(?<arg>[^ ]+))+$",
			                             RegexOptions.IgnoreCase);

			OnGenerateCode += GenerateOutput;
		}

		private void GenerateOutput(object sender, GenerationEventArgs e)
		{
			string jar = GetInstallPath(@"smc\Smc.jar");
			if (!File.Exists(jar))
			{
				e.GenerateError("Missing intalled file: " + jar);
				return;
			}

			string tempFile = Path.GetTempFileName();
			string tempDir = null;
			try
			{
				tempDir = Directory.CreateDirectory(tempFile + "_dir").FullName;

				GenerateSourceCode(jar, e, tempDir);

				DeleteAllFiles(e, tempDir);

				GenerateGraphFile(jar, e, tempDir);

				DeleteAllFiles(e, tempDir);

				//AddProjectItems(e, inputDir, names);

				EnsureDllIsInProject(e);
			}
			finally
			{
				if (tempDir != null)
				{
					DeleteAllFiles(e, tempDir);
					DeleteDir(tempDir, e);
				}

				DeleteFile(tempFile, e);
			}
		}

		private void DeleteAllFiles(GenerationEventArgs e, string tempDir)
		{
			foreach (string file in Directory.GetFiles(tempDir))
			{
				if (file == "." || file == "..")
					continue;

				DeleteFile(Path.Combine(tempDir, file), e);
			}
		}

		private void GenerateGraphFile(string jar, GenerationEventArgs e, string tempDir)
		{
			const string graphvizCommandLine = "dot";
			
			if (!ExistsOnPath(graphvizCommandLine))
			{
				return;
			}

			string inputDir = new FileInfo(e.InputFilePath).DirectoryName;

			// Generate dot file

			List<string> args = CreateArgs(e, GraphCommandLineRe, "-graph", "-glevel", "0");
			args.Insert(0, "-jar");
			args.Insert(1, jar);
			args.Add("-d");
			args.Add(tempDir);
			args.Add(e.InputFilePath);

			ProcessResult proc = Execute(tempDir, "java", args.ToArray());
			if (ProcessErrors(e, proc, e.InputFilePath))
				return;

			string dotFilename = GetGeneratedFileName(e, tempDir, "*.dot");
			if (dotFilename == null)
				return;

			// Generate image using dot

			args = CreateArgs(e, DotCommandLineRe, "-Tsvg");

			string imageExtension = (from a in args
			                         where a.StartsWith("-T")
			                         select a.Substring(2)).FirstOrDefault();
			if (imageExtension == null)
			{
				e.GenerateError("Missing -T argument for dot");
				return;
			}

			string imageFilename = Path.GetFileNameWithoutExtension(dotFilename) + "." + imageExtension;
			string imageFullPath = Path.Combine(inputDir, imageFilename);
			DeleteFile(imageFullPath, e);

			args.Add(Path.Combine(tempDir, dotFilename));
			args.Add("-o");
			args.Add(imageFullPath);
			Execute(tempDir, graphvizCommandLine, args.ToArray());

			// Copy dot to input dir
			File.WriteAllText(Path.Combine(inputDir, dotFilename), File.ReadAllText(Path.Combine(tempDir, dotFilename)));

			// Add files to solution
			var toAdd = new HashSet<string>();
			toAdd.Add(dotFilename);
			if (File.Exists(imageFullPath))
				toAdd.Add(imageFilename);

			AddProjectItems(e, inputDir, toAdd);
		}

		private static bool ExistsOnPath(string exeName)
		{
			try
			{
				using (var p = new Process())
				{
					p.StartInfo.FileName = "where";
					p.StartInfo.Arguments = exeName;

					p.StartInfo.CreateNoWindow = true;
					p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
					p.StartInfo.RedirectStandardOutput = true;
					p.StartInfo.RedirectStandardError = true;
					p.StartInfo.UseShellExecute = false;

					p.Start();
					p.WaitForExit();
					return p.ExitCode == 0;
				}
			}
			catch (Win32Exception)
			{
				throw new Exception("'where' command is not on path");
			}
		}
		
		private void GenerateSourceCode(string jar, GenerationEventArgs e, string tempDir)
		{
			List<string> args = CreateArgs(e, CommandLineRe, "-csharp", "-reflect", "-generic");
			args.Insert(0, "-jar");
			args.Insert(1, jar);
			args.Add("-d");
			args.Add(tempDir);
			args.Add(e.InputFilePath);

			ProcessResult proc = Execute(tempDir, "java", args.ToArray());
			if (ProcessErrors(e, proc, e.InputFilePath))
				return;

			// Generate output

			string generatedFilename = GetGeneratedFileName(e, tempDir, "*.cs");
			if (generatedFilename == null)
				return;

			string filename = Path.Combine(tempDir, generatedFilename);
			foreach (string line in File.ReadLines(filename))
			{
				e.OutputCode.AppendLine(line);
			}
		}

		private string GetGeneratedFileName(GenerationEventArgs e, string tempDir, string extension)
		{
			HashSet<string> names = GetFileNames(e, tempDir, extension);
			if (names.Count == 0)
			{
				e.GenerateError("Could not create result file (but no error was returned)");
				return null;
			}
			if (names.Count > 1)
			{
				e.GenerateError("Wrong number of output files created (but no error was returned): " + names.Count);
				return null;
			}

			return names.First();
		}

		private HashSet<string> GetFileNames(GenerationEventArgs e, string tempDir, string extension)
		{
			var names = new HashSet<string>();

			foreach (string file in Directory.GetFiles(tempDir, extension))
			{
				string name = new FileInfo(file).Name;
				names.Add(name);
			}

			return names;
		}

		private void AddProjectItems(GenerationEventArgs e, string dir, HashSet<string> names)
		{
			var existingNames = new HashSet<string>();

			foreach (ProjectItem item in e.ProjectItem.ProjectItems)
			{
				if (!names.Contains(item.Name))
				{
					item.Remove();
					item.Delete();
				}
				else
				{
					existingNames.Add(item.Name);
				}
			}

			foreach (string name in names)
			{
				if (existingNames.Contains(name))
					continue;

				e.ProjectItem.ProjectItems.AddFromFile(Path.Combine(dir, name));
			}
		}

		private bool ProcessErrors(GenerationEventArgs e, ProcessResult proc, string filename)
		{
			bool hasError = false;

			if (proc.StdErr.Trim() != "")
			{
				// Parse errors
				foreach (string line in proc.StdErr.Split('\r', '\n'))
				{
					string err = line.Trim();
					if (err == "")
						continue;

					hasError = true;

					if (!err.StartsWith(filename))
					{
						e.GenerateError(line);
						continue;
					}

					err = err.Substring(filename.Length);

					Match match = ErrorRe.Match(err);
					if (!match.Success)
					{
						e.GenerateError(line);
						continue;
					}

					int lineNum = Convert.ToInt32(match.Groups["line"].Value);
					if (lineNum <= 0)
						e.GenerateError(match.Groups["err"].Value);
					else
						e.GenerateError(match.Groups["err"].Value, lineNum - 1);
				}
			}

			if (!hasError && proc.Result != 0)
			{
				hasError = true;
				e.GenerateError("Error executing SMC: " + proc.Result);
			}

			return hasError;
		}

		private void EnsureDllIsInProject(GenerationEventArgs e)
		{
			try
			{
				string dll = GetInstallPath(@"smc\lib\Release\NoTrace\statemap.dll");
#if DEBUG
//				dll = @"C:\Desenvolvimento\c#\SMCVS\smc\lib\Release\NoTrace\statemap.dll";
#endif
				if (!File.Exists(dll))
					throw new FileNotFoundException("Missing intalled file", dll);

				var project = (VSProject) e.ProjectItem.ContainingProject.Object;
				bool hasRef =
					project.References.Cast<Reference>().Any(
						r =>
						r != null && string.Equals(r.Name, "statemap", StringComparison.InvariantCultureIgnoreCase));

				if (!hasRef)
				{
					Reference dllRef = project.References.Add(dll);
					dllRef.CopyLocal = true;
				}
			}
			catch (Exception ex)
			{
				e.GenerateWarning("Failed to add reference to statemap.dll:" + ex.Message);
			}
		}

		private List<string> CreateArgs(GenerationEventArgs e, Regex regex, params string[] defaults)
		{
			var lines = e.InputText.Split('\n');
			
			for (var i = 0; i < lines.Length; i++)
			{
				var line = lines[i].Trim();

				if (line.Length == 0)
				{
					continue;
				}

				if (!line.StartsWith("//"))
				{
					break;
				}

				var comment = line.Substring(2);
				var match = regex.Match(comment);

				if (!match.Success)
				{
					continue;
				}

				var args = new List<string>();
				
				foreach (var capture in match.Groups["arg"].Captures)
				{
					args.Add(capture.ToString().Trim());
				}
				
				return args;
			}

			return defaults.ToList();
		}

		private ProcessResult Execute(string workingDir, string cmd, params string[] args)
		{
			var result = new ProcessResult();

			using (var proc = new Process())
			{
				proc.StartInfo.WorkingDirectory = workingDir;
				proc.StartInfo.FileName = cmd;

				var sb = new StringBuilder();
				for (int i = 0; i < args.Length; i++)
				{
					if (i > 0)
						sb.Append(" ");
					sb.Append("\"").Append(args[i]).Append("\"");
				}
				proc.StartInfo.Arguments = sb.ToString();

				proc.StartInfo.CreateNoWindow = true;
				proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
				proc.StartInfo.RedirectStandardOutput = true;
				proc.StartInfo.RedirectStandardError = true;
				proc.StartInfo.UseShellExecute = false;

				var stdout = new StringBuilder();
				proc.OutputDataReceived +=
					(sender, e) => { if (!String.IsNullOrEmpty(e.Data)) stdout.AppendLine(e.Data); };

				proc.Start();
				proc.BeginOutputReadLine();
				result.StdErr = proc.StandardError.ReadToEnd();
				proc.WaitForExit();

				result.StdOut = stdout.ToString();
				result.Result = proc.ExitCode;
			}

			return result;
		}

		private void DeleteDir(string dir, GenerationEventArgs e)
		{
			try
			{
				Directory.Delete(dir, true);
			}
			catch (Exception ex)
			{
				e.GenerateWarning(ex.Message);
			}
		}

		private void DeleteFile(string filename, GenerationEventArgs e)
		{
			try
			{
				File.Delete(filename);
			}
			catch (Exception ex)
			{
				e.GenerateWarning(ex.Message);
			}
		}

		public static string GetInstallPath(string filename = null)
		{
			if (_installPath == null)
				_installPath = Path.GetDirectoryName(typeof (SMCTool).Assembly.Location);

			return string.IsNullOrEmpty(filename) ? _installPath : Path.Combine(_installPath, filename);
		}

		#region Nested type: ProcessResult

		private class ProcessResult
		{
			public int Result;
			public string StdErr;
			public string StdOut;
		}

		#endregion

		//#region COM Register 

		//// http://visualstudiomagazine.com/Articles/2009/03/01/Generate-Code-from-Custom-File-Formats.aspx?Page=1
		//private const string VSVersion = "10.0";
		//private const string CSLangGUID = "FAE04EC1-301F-11D3-BF4B-00C04F79EFBC";

		//private const string ToolName = "SMCTool";
		//private const string ToolDesc = "Generates code using the State Machine Compiler (SMC)";
		//private const string ToolGUID = "6FEAEE40-D6B5-4A6C-A9EB-5011E18A49E0";

		//[ComRegisterFunction]
		//public static void ComRegister(Type type)
		//{
		//    RegistryKey key =
		//        Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\" + VSVersion +
		//                                           @"\Generators\{" + CSLangGUID + @"}\" + ToolName + @"\");

		//    key.SetValue("", ToolDesc);
		//    key.SetValue("CLSID", "{" + ToolGUID + "}");
		//    key.SetValue("GeneratesDesignTimeSource", 1);
		//    //key.SetValue("GeneratesSharedDesignTimeSource", 1);
		//}

		//[ComUnregisterFunction]
		//public static void ComUnregister(Type type)
		//{
		//    Registry.LocalMachine.DeleteSubKey(
		//        @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\" + VSVersion + @"\Generators\{" + CSLangGUID +
		//        @"}\" + ToolName + @"\", false);
		//}

		//#endregion
	}
}