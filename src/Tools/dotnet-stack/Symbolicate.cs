// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Tools.Common;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.IO;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Microsoft.Diagnostics.Tools.Stack
{
    internal static class SymbolicateHandler
    {
        private static readonly Regex s_regex = new Regex(@" at (?<type>[\w+\.?]+)\.(?<method>\w+)\((?<params>.*)\) in (?<filename>[\w+\.?]+)(\.dll|\.ni\.dll): token (?<token>0x\d+)\+(?<offset>0x\d+)", RegexOptions.Compiled);
        private static readonly Dictionary<string, MetadataReader> s_metadataReaderDictionary = new Dictionary<string, MetadataReader>();

        delegate void SymbolicateDelegate(IConsole console, FileInfo inputPath, DirectoryInfo[] searchDir, FileInfo output, bool stdout);

        /// <summary>
        /// Get the line number from the Method Token and IL Offset at the stacktrace
        /// </summary>
        /// <param name="console"></param>
        /// <param name="inputPath">The input path for file with stacktrace text</param>
        /// <param name="searchDir">All paths in the directory to the assembly and pdb where the exception occurred</param>
        /// <param name="output">The output path for the extracted line number data</param>
        /// <returns></returns>
        private static void Symbolicate(IConsole console, FileInfo inputPath, DirectoryInfo[] searchDir, FileInfo output, bool stdout)
        {
            try
            {
                if (output == null)
                {
                    output = new FileInfo(inputPath.FullName + ".symbolicated");
                }

                CreateSymbolicateFile(console, searchDir, inputPath.FullName, output.FullName, stdout);
            }
            catch (Exception e)
            {
                console.Error.WriteLine(e.Message);
            }
        }

        private static void CreateSymbolicateFile(IConsole console, DirectoryInfo[] searchDir, string inputPath, string outputPath, bool isStdout)
        {
            try
            {
                SetMetadataReader(searchDir);

                string ret = string.Empty;
                using StreamWriter fileStreamWriter = new StreamWriter(new FileStream(outputPath, FileMode.Create, FileAccess.Write));
                using StreamReader fileStreamReader = new StreamReader(new FileStream(inputPath, FileMode.Open, FileAccess.Read));
                while (!fileStreamReader.EndOfStream)
                {
                    string line = fileStreamReader.ReadLine();
                    if (!s_regex.Match(line).Success)
                    {
                        fileStreamWriter?.WriteLine(ret);
                        if (isStdout) console.Out.WriteLine(ret);
                        continue;
                    }
                    ret = TrySymbolicateLine(line);
                    fileStreamWriter?.WriteLine(ret);
                    if (isStdout) console.Out.WriteLine(ret);
                }
                console.Out.WriteLine($"\nOutput: {outputPath}\n");
            }
            catch (Exception e)
            {
                console.Error.WriteLine(e.Message);
            }
        }

        private static void SetMetadataReader(DirectoryInfo[] searchDir)
        {
            List<string> searchPaths = new List<string>();
            searchPaths.Add(Directory.GetCurrentDirectory());
            foreach (var path in searchDir)
            {
                searchPaths.Add(path.FullName);
            }

            List<string> peFiles = GrabFiles(searchPaths, "*.dll");
            if (peFiles.Count == 0)
            {
                throw new FileNotFoundException("Assembly file not found\n");
            }
            peFiles = peFiles.Distinct().ToList();
            peFiles.Sort();

            List<string> pdbFiles = GrabFiles(searchPaths, "*.pdb");
            if (pdbFiles.Count == 0)
            {
                throw new FileNotFoundException("PDB file not found\n");
            }
            pdbFiles = pdbFiles.Distinct().ToList();
            pdbFiles.Sort();

            int pdbCnt = 0;
            for (int peCnt = 0; peCnt < peFiles.Count; peCnt++)
            {
                if (peFiles[peCnt].Contains(".ni.dll"))
                {
                    continue;
                }
                int compare = string.Compare(Path.GetFileNameWithoutExtension(peFiles[peCnt]), Path.GetFileNameWithoutExtension(pdbFiles[pdbCnt]), StringComparison.OrdinalIgnoreCase);
                if (compare == 0)
                {
                    SetMetadataReaderDictionary(peFiles[peCnt]);
                }
                else if (compare > 0)
                {
                    pdbCnt++;
                    peCnt--;
                }
                if (pdbCnt == pdbFiles.Count) break;
            }
        }

        private static List<string> GrabFiles(List<string> paths, string searchPattern)
        {
            List<string> files = new List<string>();
            foreach (var assemDir in paths)
            {
                if (Directory.Exists(assemDir))
                {
                    files.AddRange(Directory.GetFiles(assemDir, searchPattern, SearchOption.AllDirectories));
                }
            }
            return files;
        }

        private static void SetMetadataReaderDictionary(string filePath)
        {
            try
            {
                static Stream streamProvider(string sp) => new FileStream(sp, FileMode.Open, FileAccess.Read);
                using Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                if (stream != null)
                {
                    MetadataReaderProvider provider = null;
                    if (filePath.Contains(".dll"))
                    {
                        using PEReader peReader = new PEReader(stream);
                        if (!peReader.TryOpenAssociatedPortablePdb(filePath, streamProvider, out provider, out string pdbPath))
                        {
                            return;
                        }
                    }
                    /*else if (filePath.Contains(".pdb"))
                    {
                        provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                    }*/
                    else
                    {
                        return;
                    }
                    MetadataReader reader = provider?.GetMetadataReader();
                    s_metadataReaderDictionary.Add(Path.GetFileNameWithoutExtension(filePath), reader);
                }
            }
            catch
            {
                return;
            }
        }

        internal sealed class StackTraceInfo
        {
            public string Type;
            public string Method;
            public string Param;
            public string Assembly;
            public string Pdb;
            public string Token;
            public string Offset;
        }

        private static string TrySymbolicateLine(string line)
        {
            string ret = line;
            Match match = s_regex.Match(line);
            if (!match.Success)
            {
                return line;
            }

            StackTraceInfo stInfo = new StackTraceInfo()
            {
                Type = match.Groups["type"].Value,
                Method = match.Groups["method"].Value,
                Param = match.Groups["params"].Value,
                Assembly = match.Groups["filename"].Value,
                Token = match.Groups["token"].Value,
                Offset = match.Groups["offset"].Value
            };
            stInfo.Pdb = stInfo.Assembly.Contains(".ni.dll") ? stInfo.Assembly.Replace(".ni.dll", ".pdb") : stInfo.Assembly.Replace(".dll", ".pdb");

            return GetLineFromMetadata(TryGetMetadataReader(stInfo.Assembly), ret, stInfo);
        }

        private static MetadataReader TryGetMetadataReader(string assemblyName)
        {
            if (s_metadataReaderDictionary.ContainsKey(assemblyName))
            {
                return s_metadataReaderDictionary[assemblyName];
            }
            return null;
        }

        private static string GetLineFromMetadata(MetadataReader reader, string line, StackTraceInfo stInfo)
        {
            if (reader != null)
            {
                Handle handle = MetadataTokens.Handle(Convert.ToInt32(stInfo.Token, 16));
                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                    MethodDebugInformation methodInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                    if (!methodInfo.SequencePointsBlob.IsNil)
                    {
                        SequencePointCollection sequencePoints = methodInfo.GetSequencePoints();
                        SequencePoint? bestPointSoFar = null;
                        foreach (SequencePoint point in sequencePoints)
                        {
                            if (point.Offset > Convert.ToInt64(stInfo.Offset, 16))
                                break;

                            if (point.StartLine != SequencePoint.HiddenLine)
                                bestPointSoFar = point;
                        }

                        if (bestPointSoFar.HasValue)
                        {
                            string sourceFile = reader.GetString(reader.GetDocument(bestPointSoFar.Value.Document).Name);
                            int sourceLine = bestPointSoFar.Value.StartLine;
                            return $"   at {stInfo.Type}.{stInfo.Method}({stInfo.Param}) in {sourceFile}:line {sourceLine}";
                        }
                    }
                }
            }
            return line;
        }

        public static Command SymbolicateCommand() =>
            new Command(
                name: "symbolicate", description: "Get the line number from the Method Token and IL Offset in a stacktrace")
            {
                // Handler
                HandlerDescriptor.FromDelegate((SymbolicateDelegate)Symbolicate).GetCommandHandler(),
                // Arguments and Options
                InputFileArgument(),
                SearchDirectoryOption(),
                OutputFileOption(),
                StandardOutOption()
            };

        public static Argument<FileInfo> InputFileArgument() =>
            new Argument<FileInfo>(name: "input-path")
            {
                Description = "Path to the stacktrace text file",
                Arity = ArgumentArity.ExactlyOne
            }.ExistingOnly();

        public static Option<DirectoryInfo[]> SearchDirectoryOption() =>
            new Option<DirectoryInfo[]>(new[] { "-d", "--search-dir" }, "Path of multiple directories with assembly and pdb")
            {
                Argument = new Argument<DirectoryInfo[]>(name: "directory1 directory2 ...", getDefaultValue: () => new DirectoryInfo(Directory.GetCurrentDirectory()).GetDirectories())
                {
                    Arity = ArgumentArity.ZeroOrMore
                }.ExistingOnly()
            };

        public static Option<FileInfo> OutputFileOption() =>
            new Option<FileInfo>(new[] { "-o", "--output" }, "Output directly to a file (Default: <input-path>.symbolicated)")
            {
                Argument = new Argument<FileInfo>(name: "output-path")
                {
                    Arity = ArgumentArity.ZeroOrOne
                }
            };

        public static Option<bool> StandardOutOption() =>
            new Option<bool>(new[] { "-c", "--stdout" }, getDefaultValue: () => false, "Output directly to a console");
    }
}