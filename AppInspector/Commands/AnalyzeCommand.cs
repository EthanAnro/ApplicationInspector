﻿// Copyright (C) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt in the project root for license information.


using Microsoft.ApplicationInspector.RulesEngine;
using MultiExtractor;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.ApplicationInspector.Commands
{
    public class AnalyzeCommand : Command
    {
        public enum ExitCode
        {
            Success = 0,
            NoMatches = 1,
            CriticalError = Utils.ExitCode.CriticalError //ensure common value for final exit log mention
        }

        private readonly int WARN_ZIP_FILE_SIZE = 1024 * 1000 * 10;  // warning for large zip files 
        private readonly int MAX_FILESIZE = 1024 * 1000 * 5;  // Skip source files larger than 5 MB and log
        private readonly int MAX_TEXT_SAMPLE_LENGTH = 200;//char bytes

        private IEnumerable<string> _srcfileList;
        private AppProfile _appProfile;
        private RuleProcessor _rulesProcessor;
        private readonly HashSet<string> _uniqueTagsControl;
        private Writer _outputWriter;

        private DateTime DateScanned { get; set; }

        private DateTime _lastUpdated;

        /// <summary>
        /// Updated dynamically to more recent file in source
        /// </summary>
        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set
            {
                //find last updated file in solution
                if (_lastUpdated < value)
                {
                    _lastUpdated = value;
                }
            }
        }

        //cmdline arguments
        private readonly string _arg_sourcePath;
        private string _arg_outputFile;
        private string _arg_fileFormat;
        private readonly string _arg_outputTextFormat;
        private readonly string _arg_customRulesPath;
        private readonly bool _arg_ignoreDefaultRules;
        private readonly bool _arg_outputUniqueTagsOnly;
        private readonly bool _arg_suppressBrowserOpen;
        private readonly string _arg_confidenceFilters;
        private readonly bool _arg_simpleTagsOnly;
        private Confidence _arg_confidence;
        private readonly string _arg_consoleVerbosityLevel;
        private readonly List<string> _fileExclusionList;//see exclusion list

        public AnalyzeCommand(AnalyzeCommandOptions opt)
        {
            _arg_sourcePath = opt.SourcePath;
            _arg_outputFile = opt.OutputFilePath;
            _arg_fileFormat = opt.OutputFileFormat ?? "html";
            _arg_outputTextFormat = opt.TextOutputFormat;
            _arg_outputUniqueTagsOnly = !opt.AllowDupTags;
            _arg_customRulesPath = opt.CustomRulesPath;
            _arg_confidenceFilters = opt.ConfidenceFilters ?? "high,medium";
            _arg_consoleVerbosityLevel = opt.ConsoleVerbosityLevel ?? "medium";
            _arg_suppressBrowserOpen = opt.SuppressBrowserOpen;
            _arg_ignoreDefaultRules = opt.IgnoreDefaultRules;
            _arg_simpleTagsOnly = opt.SimpleTagsOnly;
            _arg_logger = opt.Log;
            _arg_log_file_path = opt.LogFilePath;
            _arg_log_level = opt.LogFileLevel;
            _arg_close_log_on_exit = Utils.CLIExecutionContext ? true : opt.CloseLogOnCommandExit;

            opt.FilePathExclusions ??= "sample,example,test,docs,.vs,.git";
            if (!string.IsNullOrEmpty(opt.FilePathExclusions))
            {
                _fileExclusionList = opt.FilePathExclusions.ToLower().Split(",").ToList<string>();
                if (_fileExclusionList != null && (_fileExclusionList.Contains("none") || _fileExclusionList.Contains("None")))
                {
                    _fileExclusionList.Clear();
                }
            }

            _arg_logger ??= Utils.SetupLogging(opt);
            WriteOnce.Log ??= _arg_logger;

            LastUpdated = DateTime.MinValue;
            DateScanned = DateTime.Now;

            try
            {
                ConfigureConsoleOutput();
                ConfigFileOutput();
                ConfigSourcetoScan();
                ConfigConfidenceFilters();
                ConfigRules();
            }
            catch (Exception e) //group error handling
            {
                WriteOnce.Error(e.Message);
                if (_arg_close_log_on_exit)
                {
                    Utils.Logger = null;
                    WriteOnce.Log = null;
                }
                throw;
            }

            _uniqueTagsControl = new HashSet<string>();
        }


        #region configureMethods



        /// <summary>
        /// Establish console verbosity
        /// For NuGet DLL use, console is muted overriding any arguments sent
        /// </summary>
        private void ConfigureConsoleOutput()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigureConsoleOutput", LogLevel.Trace);

            //Set console verbosity based on run context (none for DLL use) and caller arguments
            if (!Utils.CLIExecutionContext)
            {
                WriteOnce.Verbosity = WriteOnce.ConsoleVerbosity.None;
            }
            else
            {
                WriteOnce.ConsoleVerbosity verbosity = WriteOnce.ConsoleVerbosity.Medium;
                if (!Enum.TryParse(_arg_consoleVerbosityLevel, true, out verbosity))
                {
                    WriteOnce.Error(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_ARG_VALUE, "-x"));
                    throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_ARG_VALUE, "-x"));
                }
                else
                {
                    WriteOnce.Verbosity = verbosity;
                }
            }
        }


        /// <summary>
        /// Note that WriteOnce.Writer is not used for AnalyzeCommand which has specific Writer classes for formattig to html, json, text unlike
        /// other command classes to writting to a file here is handled by the AnalyzeCommand FlushAll() method 
        /// </summary>
        private void ConfigFileOutput()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigOutput", LogLevel.Trace);

            WriteOnce.FlushAll();//in case called more than once

            //error check for no output
            if (string.IsNullOrEmpty(_arg_outputFile) && _arg_consoleVerbosityLevel.ToLower() == "none")
            {
                throw new Exception(ErrMsg.GetString(ErrMsg.ID.CMD_NO_OUTPUT));
            }

            if (_arg_fileFormat == "html")
            {
                if (!string.IsNullOrEmpty(_arg_outputFile)) //dependent local files won't be there; TODO look into dir copy to target!
                {
                    _arg_outputFile = String.Empty;
                    WriteOnce.Info("output file argument ignored for html format");
                }

                if (!_arg_outputUniqueTagsOnly) //fix #183
                {
                    throw new Exception(ErrMsg.GetString(ErrMsg.ID.ANALYZE_NODUPLICATES_HTML_FORMAT));
                }

                if (_arg_simpleTagsOnly) //won't work for html that expects full data
                {
                    throw new Exception(ErrMsg.GetString(ErrMsg.ID.ANALYZE_SIMPLETAGS_HTML_FORMAT));
                }
            }

            //Set outstream
            _outputWriter = WriterFactory.GetWriter(_arg_fileFormat ?? "text", (string.IsNullOrEmpty(_arg_outputFile)) ? null : "text", _arg_outputTextFormat);
            if (!string.IsNullOrEmpty(_arg_outputFile))
            {
                _outputWriter.TextWriter = File.CreateText(_arg_outputFile);//not needed if html output since application controlled
            }
            else
            {
                _outputWriter.TextWriter = Console.Out;
            }

        }


        /// <summary>
        /// Expects user to supply all that apply
        /// </summary>
        private void ConfigConfidenceFilters()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigConfidenceFilters", LogLevel.Trace);
            //parse and verify confidence values
            if (String.IsNullOrEmpty(_arg_confidenceFilters))
            {
                _arg_confidence = Confidence.High | Confidence.Medium; //excludes low by default
            }
            else
            {
                string[] confidences = _arg_confidenceFilters.Split(',');
                foreach (string confidence in confidences)
                {
                    Confidence single;
                    if (Enum.TryParse(confidence, true, out single))
                    {
                        _arg_confidence |= single;
                    }
                    else
                    {
                        throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_ARG_VALUE, "x"));
                    }
                }
            }
        }


        /// <summary>
        /// Simple validation on source path provided for scanning and preparation
        /// </summary>
        private void ConfigSourcetoScan()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigSourcetoScan", LogLevel.Trace);

            if (String.IsNullOrEmpty(_arg_sourcePath))
            {
                throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_REQUIRED_ARG_MISSING, "SourcePath"));
            }

            if (Directory.Exists(_arg_sourcePath))
            {
                try
                {
                    _srcfileList = Directory.EnumerateFiles(_arg_sourcePath, "*.*", SearchOption.AllDirectories);
                    if (_srcfileList.Count() == 0)
                    {
                        throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));
                    }
                }
                catch (Exception)
                {
                    throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));
                }
            }
            else if (File.Exists(_arg_sourcePath)) //not a directory but make one for single flow
            {
                _srcfileList = new List<string>() { _arg_sourcePath };
            }
            else
            {
                throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, _arg_sourcePath));
            }
        }


        /// <summary>
        /// Add default and/or custom rules paths
        /// Iterate paths and add to ruleset
        /// </summary>
        private void ConfigRules()
        {
            WriteOnce.SafeLog("AnalyzeCommand::ConfigRules", LogLevel.Trace);

            RuleSet rulesSet = null;
            List<string> rulePaths = new List<string>();

            if (!_arg_ignoreDefaultRules)
            {
                rulePaths.Add(Utils.GetPath(Utils.AppPath.defaultRulesPackedFile));
                rulesSet = Utils.GetDefaultRuleSet(_arg_logger);
            }

            if (!string.IsNullOrEmpty(_arg_customRulesPath))
            {
                if (rulesSet == null)
                {
                    rulesSet = new RuleSet(_arg_logger);
                }

                rulePaths.Add(_arg_customRulesPath);

                if (Directory.Exists(_arg_customRulesPath))
                {
                    rulesSet.AddDirectory(_arg_customRulesPath);
                }
                else if (File.Exists(_arg_customRulesPath))
                {
                    rulesSet.AddFile(_arg_customRulesPath);
                }
                else
                {
                    throw new Exception(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_RULE_PATH, _arg_customRulesPath));
                }
            }

            //error check based on ruleset not path enumeration
            if (rulesSet == null || rulesSet.Count() == 0)
            {
                throw new Exception(ErrMsg.GetString(ErrMsg.ID.CMD_NORULES_SPECIFIED));
            }

            //instantiate a RuleProcessor with the added rules and exception for dependency
            _rulesProcessor = new RuleProcessor(rulesSet, _arg_confidence, _arg_outputUniqueTagsOnly, _arg_simpleTagsOnly, _arg_logger);

            if (_arg_outputUniqueTagsOnly)
            {
                List<TagException> tagExceptions;
                if (File.Exists(Utils.GetPath(Utils.AppPath.tagCounterPref)))
                {
                    tagExceptions = JsonConvert.DeserializeObject<List<TagException>>(File.ReadAllText(Utils.GetPath(Utils.AppPath.tagCounterPref)));
                    string[] exceptions = new string[tagExceptions.Count];
                    for (int i = 0; i < tagExceptions.Count; i++)
                    {
                        exceptions[i] = tagExceptions[i].Tag;
                    }

                    _rulesProcessor.UniqueTagExceptions = exceptions;
                }
            }

            _appProfile = new AppProfile(_arg_sourcePath, rulePaths, false, _arg_simpleTagsOnly, _arg_outputUniqueTagsOnly, _arg_suppressBrowserOpen);
            _appProfile.Args = "analyze -f " + _arg_fileFormat + " -u " + _arg_outputUniqueTagsOnly.ToString().ToLower() + " -v " +
                WriteOnce.Verbosity.ToString() + " -x " + _arg_confidence + " -i " + _arg_ignoreDefaultRules.ToString().ToLower();
        }


        #endregion


        /// <summary>
        /// Option for DLL use as alternate to Run() which only outputs a file to return results as string
        /// CommandOption defaults will not have been set when used as DLL via CLI processing so some checks added
        /// </summary>
        /// <returns>output results</returns>
        public override string GetResult()
        {
            _arg_fileFormat ??= "json";
            _arg_outputFile = Path.GetTempFileName();
            ConfigFileOutput();

            if ((int)Utils.ExitCode.Success == Run())
            {
                return File.ReadAllText(_arg_outputFile);
            }

            return string.Empty;
        }

        /// <summary>
        /// Main entry point to start analysis from CLI; handles setting up rules, directory enumeration
        /// file type detection and handoff
        /// Pre: All Configure Methods have been called already and we are ready to SCAN
        /// </summary>
        /// <returns></returns>
        public override int Run()
        {
            WriteOnce.SafeLog("AnalyzeCommand::Run", LogLevel.Trace);
            WriteOnce.Operation(ErrMsg.FormatString(ErrMsg.ID.CMD_RUNNING, "analyze"));

            try
            {
                _appProfile.MetaData.TotalFiles = _srcfileList.Count();//updated for zipped files later

                // Iterate through all files and process against rules
                foreach (string filename in _srcfileList)
                {
                    ArchiveFileType archiveFileType = ArchiveFileType.UNKNOWN;
                    try //fix for #146
                    {
                        archiveFileType = MiniMagic.DetectFileType(filename);
                    }
                    catch (Exception e)
                    {
                        WriteOnce.SafeLog(e.Message + "\n" + e.StackTrace, LogLevel.Error);//log details
                        Exception f = new Exception(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILE_TYPE_OPEN, filename));//report friendly version
                        throw f;
                    }

                    if (archiveFileType == ArchiveFileType.UNKNOWN)//not a known zipped file type
                    {
                        ProcessAsFile(filename);
                    }
                    else
                    {
                        UnZipAndProcess(filename, archiveFileType);
                    }
                }

                WriteOnce.General("\r" + ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILES_PROCESSED_PCNT, 100));
                WriteOnce.Operation(ErrMsg.GetString(ErrMsg.ID.CMD_PREPARING_REPORT));

                //Prepare report results
                _appProfile.MetaData.LastUpdated = LastUpdated.ToString();
                _appProfile.DateScanned = DateScanned.ToString();

                //wrapup result status
                if (_appProfile.MetaData.TotalFiles == _appProfile.MetaData.FilesSkipped)
                {
                    WriteOnce.Error(ErrMsg.GetString(ErrMsg.ID.ANALYZE_NOSUPPORTED_FILETYPES));
                }
                else if (_appProfile.MatchList.Count == 0)
                {
                    WriteOnce.Error(ErrMsg.GetString(ErrMsg.ID.ANALYZE_NOPATTERNS));
                }
                else
                {
                    _appProfile.PrepareReport();
                    FlushAll();
                }

                WriteOnce.Operation(ErrMsg.FormatString(ErrMsg.ID.CMD_COMPLETED, "analyze"));
            }
            catch (Exception e)
            {
                WriteOnce.Error(e.Message);
                //exit normaly for CLI callers and throw for DLL callers
                if (Utils.CLIExecutionContext)
                {
                    return (int)ExitCode.CriticalError;
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                if (_arg_close_log_on_exit)
                {
                    Utils.Logger = null;
                    WriteOnce.Log = null;
                }
            }

            return _appProfile.MatchList.Count() == 0 ? (int)ExitCode.NoMatches :
                (int)Utils.ExitCode.Success;
        }



        /// <summary>
        /// Wrapper for files that are on disk and ready to read vs unzipped files which are not to allow separation of core
        /// scan evaluation for use by decompression methods as well
        /// </summary>
        /// <param name="filename"></param>
        private void ProcessAsFile(string filename)
        {
            //check for supported language
            LanguageInfo languageInfo = new LanguageInfo();
            if (FileChecksPassed(filename, ref languageInfo))
            {
                LastUpdated = File.GetLastWriteTime(filename);
                _appProfile.MetaData.PackageTypes.Add(ErrMsg.GetString(ErrMsg.ID.ANALYZE_UNCOMPRESSED_FILETYPE));

                string fileText = File.ReadAllText(filename);
                ProcessInMemory(filename, fileText, languageInfo);
            }
        }




        /// <summary>
        /// Main WORKHORSE for analyzing file; called from file based or decompression functions
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="fileText"></param>
        private void ProcessInMemory(string filePath, string fileText, LanguageInfo languageInfo)
        {
            #region minorRollupTrackingAndProgress

            WriteOnce.SafeLog("Preparing to process file: " + filePath, LogLevel.Trace);

            _appProfile.MetaData.FilesAnalyzed++;

            int totalFilesReviewed = _appProfile.MetaData.FilesAnalyzed + _appProfile.MetaData.FilesSkipped;
            int percentCompleted = (int)((float)totalFilesReviewed / (float)_appProfile.MetaData.TotalFiles * 100);
            //earlier issue now resolved so app handles mixed zipped/zipped and unzipped/zipped directories but catch all for non-critical UI
            if (percentCompleted > 100)
            {
                percentCompleted = 100;
            }
            else if (percentCompleted < 100) //caller already reports @100% so avoid 2x for file output
            {
                WriteOnce.General("\r" + ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILES_PROCESSED_PCNT, percentCompleted), false);
            }

            #endregion

            //process file against rules
            Issue[] matches = _rulesProcessor.Analyze(fileText, languageInfo);

            //if any matches found for this file...
            if (matches.Count() > 0)
            {
                _appProfile.MetaData.FilesAffected++;
                _appProfile.MetaData.TotalMatchesCount += matches.Count();

                // Iterate through each match issue 
                foreach (Issue match in matches)
                {
                    WriteOnce.SafeLog(string.Format("Processing pattern matches for ruleId {0}, ruleName {1} file {2}", match.Rule.Id, match.Rule.Name, filePath), LogLevel.Trace);

                    //maintain a list of unique tags; multi-purpose but primarily for filtering -d option
                    bool dupTagFound = false;
                    foreach (string t in match.Rule.Tags)
                    {
                        dupTagFound = !_uniqueTagsControl.Add(t);
                    }

                    //save all unique dependencies even if Dependency tag pattern is not-unique
                    var tagPatternRegex = new Regex("Dependency.SourceInclude", RegexOptions.IgnoreCase);
                    String textMatch;
                    if (match.Rule.Tags.Any(v => tagPatternRegex.IsMatch(v)))
                    {
                        textMatch = ExtractDependency(fileText, match.Boundary.Index, match.PatternMatch, languageInfo.Name);
                    }
                    else
                    {
                        textMatch = ExtractTextSample(fileText, match.Boundary.Index, match.Boundary.Length);
                    }

                    //wrap rule issue result to add metadata
                    MatchRecord record = new MatchRecord()
                    {
                        Filename = filePath,
                        Language = languageInfo,
                        Filesize = fileText.Length,
                        TextSample = textMatch,
                        Excerpt = ExtractExcerpt(fileText, match.StartLocation.Line),
                        Issue = match
                    };

                    //preserve issue level characteristics as rolled up meta data of interest
                    bool addAsFeatureMatch = _appProfile.MetaData.AddStandardProperties(ref record);

                    //bail after extracting any dependency unique items IF user requested
                    if (_arg_outputUniqueTagsOnly && dupTagFound)
                    {
                        continue;
                    }
                    else if (addAsFeatureMatch)
                    {
                        _appProfile.MatchList.Add(record);
                    }
                    else
                    {
                        _appProfile.MetaData.TotalMatchesCount -= 1;//reduce e.g. tag counters only as per preferences file
                    }
                }
            }
            else
            {
                WriteOnce.SafeLog("No pattern matches detected for file: " + filePath, LogLevel.Trace);
            }

        }

        #region PostRulesMatchProcessingAssist

        /// <summary>
        /// Simple wrapper but keeps calling code consistent
        /// Do not html code result which is accomplished later before out put to report
        /// </summary>
        /// <param name="fileText"></param>
        /// <param name="index"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private string ExtractTextSample(string fileText, int index, int length)
        {
            string result = "";
            try
            {
                //some js file results may be too long for practical display
                if (length > MAX_TEXT_SAMPLE_LENGTH)
                {
                    length = MAX_TEXT_SAMPLE_LENGTH;
                }

                result = fileText.Substring(index, length).Trim();
            }
            catch (Exception)
            {
                //control the error description and continue; error in rules engine possible
                WriteOnce.SafeLog("Unexpected indexing issue in ExtractTextSample.  Process continued", LogLevel.Error);
            }

            return result;
        }

        /// <summary>
        /// Helper to special case additional processing to just get the values without the import keywords etc.
        /// and encode for html output
        /// </summary>
        /// <param name="text"></param>
        /// <param name="language"></param>
        /// <returns></returns>
        private string ExtractDependency(string text, int startIndex, SearchPattern pattern, string language)
        {
            // import value; load value; include value; 
            string rawResult = "";
            int endIndex = text.IndexOf('\n', startIndex);
            if (-1 != startIndex && -1 != endIndex)
            {
                rawResult = text.Substring(startIndex, endIndex - startIndex).Trim();

                //recreate regex used to find entire value
                Regex regex = new Regex(pattern.Pattern);
                MatchCollection matches = regex.Matches(rawResult);

                //remove surrounding import or trailing comments 
                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        if (match.Groups.Count == 1)//handles cases like "using Newtonsoft.Json"
                        {
                            string[] parseValues = match.Groups[0].Value.Split(' ');
                            if (parseValues.Length == 1)
                            {
                                rawResult = parseValues[0].Trim();
                            }
                            else if (parseValues.Length > 1)
                            {
                                rawResult = parseValues[1].Trim(); //should be value; time will tell if fullproof
                            }
                        }
                        else if (match.Groups.Count > 1)//handles cases like include <stdio.h>
                        {
                            rawResult = match.Groups[1].Value.Trim();
                        }
                        //else if > 2 too hard to match; do nothing

                        break;//only designed to expect one match per line i.e. not include value include value
                    }
                }

                String finalResult = rawResult.Replace(";", "");
                _appProfile.MetaData.UniqueDependencies.Add(finalResult);

                return System.Net.WebUtility.HtmlEncode(finalResult);
            }

            return rawResult;
        }

        /// <summary>
        /// Located here to include during Match creation to avoid a call later or putting in constructor
        /// Needed in match ensuring value exists at time of report writing rather than expecting a callback
        /// from the template
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="startLineNumber"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private string ExtractExcerpt(string text, int startLineNumber, int length = 10)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            var lines = text.Split('\n');
            var distance = (int)((length - 1.0) / 2.0);

            // Sanity check
            if (startLineNumber < 0)
            {
                startLineNumber = 0;
            }

            if (startLineNumber >= lines.Length)
            {
                startLineNumber = lines.Length - 1;
            }

            var excerptStartLine = Math.Max(0, startLineNumber - distance);
            var excerptEndLine = Math.Min(lines.Length - 1, startLineNumber + distance);

            /* This is a little wacky, but if the code snippet we're viewing is already
             * indented 16 characters minimum, we don't want to show all that extra white-
             * space, so we'll find the smallest number of spaces at the beginning of
             * each line and use that.
             */

            var minSpaces = -1;
            for (var i = excerptStartLine; i <= excerptEndLine; i++)
            {
                var numPrefixSpaces = lines[i].TakeWhile(c => c == ' ').Count();
                minSpaces = (minSpaces == -1 || numPrefixSpaces < minSpaces) ? numPrefixSpaces : minSpaces;
            }

            var sb = new StringBuilder();
            // We want to go from (start - 5) to (start + 5) (off by one?)
            // LINE=10, len=5, we want 8..12, so N-(L-1)/2 to N+(L-1)/2
            // But cap those values at 0/end
            for (var i = excerptStartLine; i <= excerptEndLine; i++)
            {
                string line = lines[i].Substring(minSpaces).TrimEnd();
                sb.AppendLine(line);
                //string line = System.Net.WebUtility.HtmlEncode(lines[i].Substring(minSpaces).TrimEnd());
                //sb.AppendFormat("{0}  {1}\n", (i + 1).ToString().PadLeft(n, ' '), line);
            }

            return System.Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }



        public void FlushAll()
        {
            if (_outputWriter != null)
            {
                _outputWriter.WriteApp(_appProfile);

                if (_outputWriter.TextWriter != null)
                {
                    if (_arg_fileFormat != "html")
                    {
                        _outputWriter.FlushAndClose();//not required for htmt formal i.e. already closed
                        _outputWriter = null;
                        WriteOnce.Writer = null;

                        //Special case to avoid writing tmp file path to output file for TagTest,TagDiff or when called as a DLL since unnecessary
                        if (_arg_consoleVerbosityLevel.ToLower() != "none")
                        {
                            if (!String.IsNullOrEmpty(_arg_outputFile) && Utils.CLIExecutionContext)
                            {
                                WriteOnce.Info(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_OUTPUT_FILE, _arg_outputFile), true, WriteOnce.ConsoleVerbosity.Medium, false);
                            }
                            else
                            {
                                WriteOnce.NewLine();
                            }
                        }
                    }
                }
            }
        }

        #endregion


        private void UnZipAndProcess(string filename, ArchiveFileType archiveFileType)
        {
            // zip itself may be in excluded list i.e. sample, test or similar unless ignore filter requested
            if (_fileExclusionList != null && _fileExclusionList.Any(v => filename.ToLower().Contains(v)))
            {
                WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_EXCLUDED_TYPE_SKIPPED, filename), LogLevel.Warn);
                _appProfile.MetaData.FilesSkipped++;
                return;
            }

            //zip itself may be too huge for timely processing
            if (new FileInfo(filename).Length > WARN_ZIP_FILE_SIZE)
            {
                WriteOnce.General(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_COMPRESSED_FILESIZE_WARN));
            }
            else
            {
                WriteOnce.General(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_COMPRESSED_PROCESSING));
            }

            LastUpdated = File.GetLastWriteTime(filename);
            _appProfile.MetaData.PackageTypes.Add(ErrMsg.GetString(ErrMsg.ID.ANALYZE_COMPRESSED_FILETYPE));

            try
            {
                IEnumerable<FileEntry> files = Extractor.ExtractFile(filename);

                if (files.Count() > 0)
                {
                    _appProfile.MetaData.TotalFiles += files.Count();//additive in case additional child zip files processed

                    foreach (FileEntry file in files)
                    {
                        //check uncompressed file passes standard checks 
                        LanguageInfo languageInfo = new LanguageInfo();
                        if (FileChecksPassed(file.FullPath, ref languageInfo, file.Content.Length))
                        {
                            byte[] streamByteArray = file.Content.ToArray();
                            ProcessInMemory(file.FullPath, Encoding.UTF8.GetString(streamByteArray, 0, streamByteArray.Length), languageInfo);
                        }
                    }
                }
                else
                {
                    WriteOnce.SafeLog(string.Format("Decompression found no files in {0}", filename), LogLevel.Warn);//zero results can be valid
                }

            }
            catch (Exception)
            {
                string errmsg = ErrMsg.FormatString(ErrMsg.ID.ANALYZE_COMPRESSED_ERROR, filename);
                WriteOnce.Error(errmsg);
                throw;
            }

        }





        /// <summary>
        /// Common validation called by ProcessAsFile and UnzipAndProcess to ensure same order and checks made
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="languageInfo"></param>
        /// <param name="fileLength">should be > zero if called from unzip method</param>
        /// <returns></returns>
        private bool FileChecksPassed(string filePath, ref LanguageInfo languageInfo, long fileLength = 0)
        {
            _appProfile.MetaData.FileExtensions.Add(Path.GetExtension(filePath).Replace('.', ' ').TrimStart());

            // 1. Skip files written in unknown language
            if (!Language.FromFileName(filePath, ref languageInfo))
            {
                WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_LANGUAGE_NOTFOUND, filePath), LogLevel.Warn);
                _appProfile.MetaData.FilesSkipped++;
                return false;
            }

            _appProfile.MetaData.AddLanguage(languageInfo.Name);

            // 2. Skip excluded files i.e. sample, test or similar unless ignore filter requested
            if (_fileExclusionList != null && _fileExclusionList.Any(v => filePath.ToLower().Contains(v)))
            {
                WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_EXCLUDED_TYPE_SKIPPED, filePath), LogLevel.Warn);
                _appProfile.MetaData.FilesSkipped++;
                return false;
            }

            // 3. Skip if exceeds file size limits
            try
            {
                fileLength = fileLength <= 0 ? new FileInfo(filePath).Length : fileLength;
                if (fileLength > MAX_FILESIZE)
                {
                    WriteOnce.SafeLog(ErrMsg.FormatString(ErrMsg.ID.ANALYZE_FILESIZE_SKIPPED, filePath), LogLevel.Warn);
                    _appProfile.MetaData.FilesSkipped++;
                    return false;
                }
            }
            catch (Exception)
            {
                WriteOnce.Error(ErrMsg.FormatString(ErrMsg.ID.CMD_INVALID_FILE_OR_DIR, filePath));
                throw;
            }

            return true;

        }
    }

}



