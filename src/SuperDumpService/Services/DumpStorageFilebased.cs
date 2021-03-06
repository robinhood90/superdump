﻿using Newtonsoft.Json;
using SuperDumpService.Models;
using SuperDumpService.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SuperDump.Models;
using Microsoft.Extensions.Options;
using SuperDumpModels;

namespace SuperDumpService.Services {
	/// <summary>
	/// for writing and reading of dumps only
	/// this implementation uses simple filebased storage
	/// </summary>
	public class DumpStorageFilebased : IDumpStorage {
		private readonly PathHelper pathHelper;
		private readonly IOptions<SuperDumpSettings> settings;

		public DumpStorageFilebased(PathHelper pathHelper, IOptions<SuperDumpSettings> settings) {
			this.pathHelper = pathHelper;
			this.settings = settings;
		}

		public async Task<IEnumerable<DumpMetainfo>> ReadDumpMetainfoForBundle(string bundleId) {
			var list = new List<DumpMetainfo>();
			foreach (var dir in Directory.EnumerateDirectories(pathHelper.GetBundleDirectory(bundleId))) {
				var dumpId = new DirectoryInfo(dir).Name;
				var id = DumpIdentifier.Create(bundleId, dumpId);
				var metainfoFilename = pathHelper.GetDumpMetadataPath(id);
				if (!File.Exists(metainfoFilename)) {
					// backwards compatibility, when Metadata files did not exist. read full json, then store metadata file
					await CreateMetainfoForCompat(id);
				}
				DumpMetainfo dumpMetainfo = ReadMetainfoFile(metainfoFilename);
				if (settings.Value.IsDumpRetentionEnabled() && dumpMetainfo.PlannedDeletionDate == null) {
					// backwards compatibility, when deletion date field does not yet exist.
					SetPlannedDeletionDateForCompat(dumpMetainfo);
				}
				dumpMetainfo.IsPrimaryDumpAvailable = ReadIsPrimaryDumpAvailable(dumpMetainfo);
				list.Add(dumpMetainfo);
			}
			return list;
		}

		public bool ReadIsPrimaryDumpAvailable(DumpMetainfo dumpMetainfo) {
			return File.Exists(GetDumpFilePath(dumpMetainfo));
		}

		private DumpMetainfo ReadMetainfoFile(string filename) {
			return JsonConvert.DeserializeObject<DumpMetainfo>(File.ReadAllText(filename));
		}

		private DumpMetainfo ReadMetainfoFile(DumpIdentifier id) {
			return ReadMetainfoFile(pathHelper.GetDumpMetadataPath(id));
		}

		private void WriteMetainfoFile(DumpMetainfo metaInfo, string filename) {
			File.WriteAllText(filename, JsonConvert.SerializeObject(metaInfo, Formatting.Indented));
		}

		public bool MiniInfoExists(DumpIdentifier id) {
			return File.Exists(pathHelper.GetDumpMiniInfoPath(id));
		}

		// throws if miniinfo not found
		public async Task<DumpMiniInfo> ReadMiniInfo(DumpIdentifier id) {
			return JsonConvert.DeserializeObject<DumpMiniInfo>(await File.ReadAllTextAsync(pathHelper.GetDumpMiniInfoPath(id)));
		}

		private async Task CreateMetainfoForCompat(DumpIdentifier id) {
			var metainfo = new DumpMetainfo() {
				BundleId = id.BundleId,
				DumpId = id.DumpId
			};
			var result = await ReadResults(id);
			if (result != null) {
				metainfo.Status = DumpStatus.Finished;
				metainfo.DumpFileName = result.AnalysisInfo.Path?.Replace(pathHelper.GetUploadsDir(), ""); // AnalysisInfo.FileName used to store full file names. e.g. "C:\superdump\uploads\myzipfilename\subdir\dump.dmp". lets only keep "myzipfilename\subdir\dump.dmp"
				metainfo.Created = result.AnalysisInfo.ServerTimeStamp;
			} else {
				metainfo.Status = DumpStatus.Failed;
			}

			WriteMetainfoFile(metainfo, pathHelper.GetDumpMetadataPath(id));
		}

		private void SetPlannedDeletionDateForCompat(DumpMetainfo dumpMetainfo) {
			dumpMetainfo.PlannedDeletionDate = dumpMetainfo.Created + TimeSpan.FromDays(settings.Value.DumpRetentionDays);
			WriteMetainfoFile(dumpMetainfo, pathHelper.GetDumpMetadataPath(dumpMetainfo.Id));
		}

		public async Task<SDResult> ReadResults(DumpIdentifier id) {
			try {
				return await ReadResultsAndThrow(id);
			} catch (Exception e) {
				Console.Error.WriteLine(e.Message);
				return null;
			}
		}

		public async Task<SDResult> ReadResultsAndThrow(DumpIdentifier id) {
			var filename = pathHelper.GetJsonPath(id);
			if (!File.Exists(filename)) {
				// fallback for older dumps
				filename = pathHelper.GetJsonPathFallback(id);
				if (!File.Exists(filename)) return null;
			}
			try {
				string text = await File.ReadAllTextAsync(filename);
				return JsonConvert.DeserializeObject<SDResult>(text, 
					new SDSystemContextConverter(), new SDModuleConverter(), new SDCombinedStackFrameConverter(), new SDTagConverter());
			} catch (Exception e) {
				string error = $"could not deserialize {filename}: {e.Message}";
				throw new Exception(error, e);
			}
		}

		public void WriteResult(DumpIdentifier id, SDResult result) {
			string filename = pathHelper.GetJsonPath(id);
			try {
				result.WriteResultToJSONFile(filename);
			} catch (Exception e) {
				string error = $"could not write result for {id} exception {e.Message}";
				Console.Error.WriteLine(error);
			}
		}

		public string GetDumpFilePath(DumpIdentifier id) {
			return GetDumpFilePath(GetSDFileInfos(id));
		}

		public string GetDumpFilePath(DumpMetainfo dumpInfo) {
			return GetDumpFilePath(GetSDFileInfos(dumpInfo));
		}

		private string GetDumpFilePath(IEnumerable<SDFileInfo> fileInfos) {
			var filename = fileInfos.FirstOrDefault(x => x.FileEntry.Type == SDFileType.PrimaryDump)?.FileInfo;
			if (filename == null) return null;
			if (!filename.Exists) return null;
			return filename.FullName;
		}

		/// <summary>
		/// actually copies a file into the dumpdirectory
		/// </summary>
		public async Task<FileInfo> AddFileCopy(DumpIdentifier id, FileInfo sourcePath) {
			return await Utility.CopyFile(sourcePath, new FileInfo(Path.Combine(pathHelper.GetDumpDirectory(id), sourcePath.Name)));
		}

		public void DeleteDumpFile(DumpIdentifier id) {
			File.Delete(GetDumpFilePath(id));
		}

		public void Create(DumpIdentifier id) {
			string dir = pathHelper.GetDumpDirectory(id);
			if (Directory.Exists(dir)) {
				throw new DirectoryAlreadyExistsException("Cannot create '{dir}'. It already exists.");
			}
			Directory.CreateDirectory(dir);
		}

		public async Task StoreMiniInfo(DumpIdentifier id, DumpMiniInfo miniInfo) {
			await File.WriteAllTextAsync(pathHelper.GetDumpMiniInfoPath(id), JsonConvert.SerializeObject(miniInfo, Formatting.None));
		}

		public void Store(DumpMetainfo dumpInfo) {
			WriteMetainfoFile(dumpInfo, pathHelper.GetDumpMetadataPath(dumpInfo.Id));
		}

		public IEnumerable<SDFileInfo> GetSDFileInfos(DumpIdentifier id) {
			return GetSDFileInfos(ReadMetainfoFile(id));
		}

		public IEnumerable<SDFileInfo> GetSDFileInfos(DumpMetainfo dumpInfo) {	
			foreach (var filePath in Directory.EnumerateFiles(pathHelper.GetDumpDirectory(dumpInfo.Id))) {
				// in case the requested file has a "special" entry in FileEntry list, add that information
				FileInfo fileInfo = new FileInfo(filePath);
				SDFileEntry fileEntry = GetSDFileEntry(dumpInfo, fileInfo);
				
				yield return new SDFileInfo() {
					FileInfo = fileInfo,
					FileEntry = fileEntry,
					SizeInBytes = fileInfo.Length,
					Downloadable = fileEntry.Type != SDFileType.PrimaryDump || settings.Value.DumpDownloadable
				};
			}
		}
		
		private SDFileEntry GetSDFileEntry(DumpMetainfo dumpInfo, FileInfo fileInfo) {
			// the file should be registered in dumpInfo
			SDFileEntry fileEntry = dumpInfo.Files.Where(x => x.FileName == fileInfo.Name).FirstOrDefault(); // due to a bug, multiple entries for the same file could exist
			if (fileEntry != null) return fileEntry;

			// but if it's not registered, do some heuristic to figure out which type of file it is.
			fileEntry = new SDFileEntry() {
				FileName = fileInfo.Name
			};
			if (Path.GetFileName(pathHelper.GetJsonPath(dumpInfo.Id)) == fileInfo.Name) {
				fileEntry.Type = SDFileType.SuperDumpMetaData;
				return fileEntry;
			}
			if (Path.GetFileName(pathHelper.GetDumpMetadataPath(dumpInfo.Id)) == fileInfo.Name) {
				fileEntry.Type = SDFileType.SuperDumpMetaData;
				return fileEntry;
			}
			if (Path.GetFileName(pathHelper.GetDumpMiniInfoPath(dumpInfo.Id)) == fileInfo.Name) {
				fileEntry.Type = SDFileType.SuperDumpMetaData;
				return fileEntry;
			}
			if (Path.GetFileName(pathHelper.GetRelationshipsPath(dumpInfo.Id)) == fileInfo.Name) {
				fileEntry.Type = SDFileType.SuperDumpMetaData;
				return fileEntry;
			}
			if ("windbg.log" == fileInfo.Name) {
				fileEntry.Type = SDFileType.WinDbg;
				return fileEntry;
			}
			if (fileInfo.Extension == ".log") {
				fileEntry.Type = SDFileType.SuperDumpLogfile;
				return fileEntry;
			}
			if (fileInfo.Extension == ".json") {
				fileEntry.Type = SDFileType.SuperDumpData;
				return fileEntry;
			}
			if (fileInfo.Extension == ".dmp") {
				fileEntry.Type = SDFileType.PrimaryDump;
				return fileEntry;
			}
			if (fileInfo.Name.EndsWith(".core.gz", StringComparison.OrdinalIgnoreCase)) {
				fileEntry.Type = SDFileType.PrimaryDump;
				return fileEntry;
			}
			if (fileInfo.Name.EndsWith(".core", StringComparison.OrdinalIgnoreCase)) {
				fileEntry.Type = SDFileType.PrimaryDump;
				return fileEntry;
			}
			if (fileInfo.Name.EndsWith(".libs.tar.gz", StringComparison.OrdinalIgnoreCase)) {
				fileEntry.Type = SDFileType.LinuxLibraries;
				return fileEntry;
			}
			// can't figure out filetype
			fileEntry.Type = SDFileType.Other;
			return fileEntry;
		}

		public FileInfo GetFile(DumpIdentifier id, string filename) {
			// make sure filename is not some relative ".."
			if (filename.Contains("..")) throw new UnauthorizedAccessException();

			string dir = pathHelper.GetDumpDirectory(id);
			var file = new FileInfo(Path.Combine(dir, filename));

			// make sure file really is inside of the dumps-directory
			if (!file.FullName.ToLower().Contains(dir.ToLower())) throw new UnauthorizedAccessException();

			var dumpInfo = ReadMetainfoFile(id);
			SDFileEntry fileEntry = GetSDFileEntry(dumpInfo, file);

			if (fileEntry.Type == SDFileType.PrimaryDump && !settings.Value.DumpDownloadable) {
				throw new UnauthorizedAccessException();
			}

			if (file.Exists) {
				return file;
			} else {
				return null;
			}
		}
	}
}
