using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Xml;

namespace Codevoid.Utility.FileDeduper
{
    class DirectoryNode
    {
        internal DirectoryNode(string name, DirectoryNode parent)
        {
            this.Name = name;
            this.Files = new Dictionary<string, FileNode>();
            this.Directories = new Dictionary<string, DirectoryNode>();
            this.Parent = parent;
        }

        internal string Name { get; }
        internal IDictionary<string, FileNode> Files { get; }
        internal IDictionary<string, DirectoryNode> Directories { get; }
        internal DirectoryNode Parent { get; }
    }

    class FileNode
    {
        internal string Name { get; }
        internal DirectoryNode Parent { get; }
        private string _hashAsString;
        private byte[] _hash;

        internal FileNode(string name, DirectoryNode parent)
        {
            this.Name = name;
            this.Parent = parent;
        }

        internal byte[] Hash
        {
            get { return this._hash; }
            set
            {
                this._hash = value;
                this._hashAsString = null;
            }
        }

        internal string HashAsString
        {
            get
            {
                if (this._hash == null)
                {
                    return null;
                }

                if (this._hashAsString == null)
                {
                    this._hashAsString = BitConverter.ToString(this._hash).Replace("-", "");
                }

                return this._hashAsString;
            }
        }
    }

    class Duplicates
    {
        internal FileNode FirstFile;
        internal IList<FileNode> DuplicateFiles { get;} = new List<FileNode>();
        internal bool HasDuplicates
        {
            get
            {
                return (this.DuplicateFiles.Count > 0);
            }
        }
    }

    // Unabashedly lifted from StackOverflow:
    // http://stackoverflow.com/questions/7244699/gethashcode-on-byte-array/7244729#7244729
    public sealed class ArrayEqualityComparer<T> : IEqualityComparer<T[]>
    {
        // You could make this a per-instance field with a constructor parameter
        private static readonly EqualityComparer<T> s_elementComparer
            = EqualityComparer<T>.Default;

        public bool Equals(T[] first, T[] second)
        {
            if (first == second)
            {
                return true;
            }

            if (first == null || second == null)
            {
                return false;
            }

            if (first.Length != second.Length)
            {

                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (!s_elementComparer.Equals(first[i], second[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(T[] array)
        {
            unchecked
            {
                if (array == null)
                {
                    return 0;
                }
                int hash = 17;
                foreach (T element in array)
                {
                    hash = hash * 31 + s_elementComparer.GetHashCode(element);
                }
                return hash;
            }
        }
    }

    class Program
    {
        /// <summary>
        /// The number of successful hashes to compute before saving state "mid stream"
        /// </summary>
        private const ulong NUMBER_OF_HASHES_BEFORE_SAVING_STATE = 50000;

        static void Main(string[] args)
        {
            var app = new Program();
            var parsedArgs = app.ParseArgs(args);
            if (!parsedArgs)
            {
                Program.PrintUsage();
                return;
            }

            if(!app.ValidateReadyToBegin())
            {
                return;
            }

            // Reduce flicker when we update the processed file count
            Console.CursorVisible = false;

            app.ListenForCancellation();
            app.Begin();
        }

        private DirectoryInfo _root;
        private DirectoryInfo _duplicateDestinationRoot;
        private DirectoryNode _rootNode = new DirectoryNode(String.Empty, null);
        private bool _resume;
        private string _statePath = "state.xml";
        private bool _skipFileSystemCheck;
        private bool _wasCancelled;
        private readonly Queue<FileNode> _itemsRequiringHashing = new Queue<FileNode>(5000);
        private readonly IDictionary<byte[], Duplicates> _hashToDuplicates = new Dictionary<byte[], Duplicates>(new ArrayEqualityComparer<byte>());
        private HashAlgorithm _hasher;
        private readonly string _rootPathPrefix = String.Empty;

        private Program()
        {
            if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                this._rootPathPrefix = @"\\?\";
            }
        }

        private bool ParseArgs(string[] args)
        {
            if (args.Length < 2)
            {
                return false;
            }

            for (var argIndex = 0; argIndex < args.Length; argIndex++)
            {
                var argument = args[argIndex].ToLowerInvariant();
                switch (argument)
                {
                    case "/r":
                    case "/root":
                        argIndex++;
                        this._root = new DirectoryInfo(this._rootPathPrefix + args[argIndex]);
                        break;

                    case "/res":
                    case "/resume":
                        this._resume = true;
                        break;

                    case "/st":
                    case "/state":
                        argIndex++;
                        this._statePath = args[argIndex];
                        break;

                    case "/skip":
                        this._skipFileSystemCheck = true;
                        break;

                    case "/d":
                    case "/destinationroot":
                        argIndex++;
                        this._duplicateDestinationRoot = new DirectoryInfo(this._rootPathPrefix + args[argIndex]);
                        break;


                    default:
                        break;
                }
            }

            return true;
        }

        private bool ValidateReadyToBegin()
        {
            if(!this._root.Exists)
            {
                Console.WriteLine($"Root directory '${this._root.FullName}' wasn't found");
                return false;
            }

            if(this._duplicateDestinationRoot != null && !this._duplicateDestinationRoot.Exists)
            {
                try
                {
                    this._duplicateDestinationRoot.Create();
                }
                catch(IOException)
                {
                    Console.WriteLine($"Unable to create directory for duplicates at '${this._duplicateDestinationRoot.FullName}'");
                    return false;
                }
            }

            return true;
        }

        private void ListenForCancellation()
        {
            Console.CancelKeyPress += this.Console_CancelKeyPress;
        }

        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            lock (this)
            {
                this._wasCancelled = true;
                e.Cancel = true;
            }
        }

        private void Begin()
        {
            Program.PrintHeader();

            Console.WriteLine("Root: {0}", this._root);

            DateTime start = DateTime.Now;
            Console.WriteLine("Started At: {0}", start);

            if (this._resume && File.Exists(this._statePath))
            {
                Console.WriteLine("Loading Saved State");
                this.LoadState(this._statePath);
                Console.WriteLine("State Loaded In: {0}", DateTime.Now - start);
            }
            else if (this._resume)
            {
                Console.WriteLine("State File not found, loading information from the file system");
            }

            ulong addedFileCount = 0;
            var cancelled = false;

            // Discover files from the file system
            if (!this._skipFileSystemCheck)
            {
                // Validate the state against the file system.
                var directories = new Queue<DirectoryInfo>();
                directories.Enqueue(this._root);

                while (directories.Count > 0)
                {
                    var directory = directories.Dequeue();

                    // If the directory is somewhere under our destination for moving
                    // then exclude that directory.
                    if(this._duplicateDestinationRoot != null && directory.FullName.StartsWith(this._duplicateDestinationRoot.FullName))
                    {
                        continue;
                    }

                    // Scavange all the directories we need to look at
                    // and add them to the queue to be processed
                    IEnumerable<DirectoryInfo> childDirectories;
                    try
                    {
                        childDirectories = directory.EnumerateDirectories();
                    }
                    // We need to catch these since there are many
                    // folders we might not have access to or might
                    // not be enumeratable
                    catch (UnauthorizedAccessException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }

                    foreach (var childDir in childDirectories)
                    {
                        directories.Enqueue(childDir);
                    }

                    IEnumerable<FileInfo> childFiles = directory.EnumerateFiles();

                    // Check the files to see if we already have information about them
                    foreach (var childFile in childFiles)
                    {
                        lock (this)
                        {
                            if (this._wasCancelled)
                            {
                                cancelled = true;
                                break;
                            }
                        }

                        if (!this.FileExistsInLoadedState(childFile.FullName))
                        {
                            // File needs to added to the tree, so process it
                            this.AddFileToLoadedState(childFile.FullName);
                            addedFileCount++;
                        }
                    }

                    if (cancelled)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Execution cancelled: Saving state for resuming later");
                        break;
                    }

                    if (addedFileCount > 0)
                    {
                        Program.UpdateConsole("New Files added: {0}", addedFileCount.ToString());
                    }
                }

                Console.WriteLine();
                Console.WriteLine("State Validated in: {0}", DateTime.Now - start);
            }

            if (addedFileCount > 0)
            {
                this.SaveCurrentStateToDisk();
            }

            // If we were cancelled, lets not continue on to process
            // the file hashes 'cause the customer is implying we
            // should give up
            if (cancelled)
            {
                return;
            }

            ulong filesHashed = 0;
            if (this._itemsRequiringHashing.Count > 0)
            {
                var hashingStart = DateTime.Now;
                Console.WriteLine("Hashing {0} File(s). Starting at: {1}", this._itemsRequiringHashing.Count, hashingStart);

                this._hasher = new MD5CryptoServiceProvider();
                ulong filesHashedSinceLastSave = 0;

                // Any items that reuqired hashing have been added to the queue
                // or been placed in the duplicate list, so lets hash the ones
                // that require work
                while (this._itemsRequiringHashing.Count > 0)
                {
                    if(filesHashedSinceLastSave > Program.NUMBER_OF_HASHES_BEFORE_SAVING_STATE)
                    {
                        this.SaveCurrentStateToDisk();
                        filesHashedSinceLastSave = 0;
                    }

                    var fileToHash = this._itemsRequiringHashing.Dequeue();

                    this.HashFileAndUpdateState(fileToHash);

                    filesHashed++;
                    filesHashedSinceLastSave++;

                    lock (this)
                    {
                        if (this._wasCancelled)
                        {
                            Console.WriteLine();
                            cancelled = true;
                            break;
                        }
                    }
                }

                if (filesHashed > 0)
                {
                    this.SaveCurrentStateToDisk();
                }
            }
            else
            {
                Console.WriteLine("No files needed hashing");
            }

            if (cancelled)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Hashing {0} file(s) took {1}", filesHashed, DateTime.Now - start);

            var filesWithDuplicates = new Queue<Duplicates>();
            var duplicateFiles = 0;

            // Calculate Duplicate Statistics
            foreach (var kvp in this._hashToDuplicates)
            {
                if (kvp.Value.HasDuplicates)
                {
                    filesWithDuplicates.Enqueue(kvp.Value);
                    duplicateFiles += kvp.Value.DuplicateFiles.Count;
                }
            }

            if (filesWithDuplicates.Count == 0)
            {
                Console.WriteLine("No duplicate files found");
                return;
            }

            Console.WriteLine("Files with duplicates: {0}", filesWithDuplicates.Count);
            Console.WriteLine("Total Duplicates: {0}", duplicateFiles);

            // If theres no destination directory, then we can't move anything
            if (this._duplicateDestinationRoot != null)
            {
                ulong filesMoved = 0;
                this._duplicateDestinationRoot.Create();
                while (filesWithDuplicates.Count > 0)
                {
                    var duplicateList = filesWithDuplicates.Dequeue();

                    filesMoved += this.MoveDuplicatesToDestinationTree(duplicateList.DuplicateFiles);

                    lock (this)
                    {
                        if (this._wasCancelled)
                        {
                            Console.WriteLine();
                            cancelled = true;
                            break;
                        }
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Files moved: {0}", filesMoved);
            }
        }

        private ulong MoveDuplicatesToDestinationTree(IList<FileNode> duplicateList)
        {
            ulong filesMoved = 0;
            foreach(var file in duplicateList)
            {
                lock(this)
                {
                    if(this._wasCancelled)
                    {
                        return filesMoved;
                    }
                }

                var destinationSubPath = Program.GetPathForDirectory(file.Parent);
                var treeSubPath = Path.Combine(destinationSubPath, file.Name);

                var sourceFilePath = Path.Combine(this._root.FullName, treeSubPath);
                if(!File.Exists(sourceFilePath))
                {
                    Console.WriteLine("Skipping File, source no longer present: {0}", sourceFilePath);
                    continue;
                }

                var destinationFilePath = Path.Combine(this._duplicateDestinationRoot.FullName, treeSubPath);
                this._duplicateDestinationRoot.CreateSubdirectory(destinationSubPath);
                File.Move(sourceFilePath, destinationFilePath);
                filesMoved++;

                Program.UpdateConsole("Moved to duplicate directory: {0}", sourceFilePath);
            }

            return filesMoved;
        }

        private void HashFileAndUpdateState(FileNode fileToHash)
        {
            var filePath = Path.Combine(this._root.FullName, Program.GetPathForDirectory(fileToHash.Parent), fileToHash.Name);
            Program.UpdateConsole("Hashing File: {0}", filePath);

            try
            {
                using (var fileStream = File.OpenRead(filePath))
                {
                    fileToHash.Hash = this._hasher.ComputeHash(fileStream);
                }
            }
            catch(SecurityException)
            {
                return;
            }
            catch(FileNotFoundException)
            {
                return;
            }
            catch (IOException)
            {
                Console.WriteLine();
                Console.WriteLine("Couldn't hash: {0}", filePath);
                return;
            }

            this.AddFileToHashOrQueueForHashing(fileToHash);
        }

        private void SaveCurrentStateToDisk()
        {
            // Write the loaded data to disk
            var state = new XmlDocument();
            var rootOfState = state.CreateElement("State");
            rootOfState.SetAttribute("GeneratedAt", DateTime.Now.ToString());
            state.AppendChild(rootOfState);

            Program.AddFilesIntoSavedState(this._rootNode.Directories.Values, this._rootNode.Files.Values, rootOfState);

            state.Save(this._statePath);
        }

        private void AddFileToLoadedState(string path)
        {
            var workingPath = path.Remove(0, this._root.FullName.Length);

            if (String.IsNullOrEmpty(workingPath))
            {
                // This can happen for the root path
                return;
            }

            // Remove any leading path separators from the working path
            if (workingPath[0] == Path.DirectorySeparatorChar)
            {
                workingPath = workingPath.Remove(0, 1);
            }

            // This will include the extension, if there is one
            string fileName = Path.GetFileName(path);

            // This is a file, so we need to strip out the filename
            // from the path we're looking up to ensure that we find
            // the right parent directory            
            workingPath = workingPath.Remove(workingPath.Length - fileName.Length, fileName.Length);

            // Trailing path separators will stop us from finding the path correctly
            if (workingPath.EndsWith(Path.DirectorySeparatorChar))
            {
                workingPath = workingPath.TrimEnd(new char[] { Path.DirectorySeparatorChar });
            }

            // Start our search at the root
            var current = this._rootNode;

            // Break out the path into the individual folder parts
            var components = workingPath.Split(Path.DirectorySeparatorChar);
            foreach (string component in components)
            {
                if (String.IsNullOrEmpty(component))
                {
                    continue;
                }

                // If any part of the path isn't found in the
                // dictionaries, we need to fill in the missing parts
                if (!current.Directories.TryGetValue(component, out DirectoryNode directory)) {
                    directory = new DirectoryNode(component, current);
                    current.Directories[component] = directory;
                }

                current = directory;
            }

            // Since we're looking a file, we can assume that the
            // dictionary looking up will give us the conclusive
            // answer (since we found the folder path already)
            var newFile = new FileNode(fileName, current);
            current.Files[fileName] = newFile;
            this.AddFileToHashOrQueueForHashing(newFile);
        }

        private bool FileExistsInLoadedState(string path)
        {
            if (this._rootNode.Files.Count == 0 && this._rootNode.Directories.Count == 0)
            {
                return false;
            }

            var workingPath = path.Remove(0, this._root.FullName.Length);

            if (String.IsNullOrEmpty(workingPath))
            {
                // This is the root node, by definition
                return true;
            }

            // Remove any leading path separators from the working path
            if (workingPath[0] == Path.DirectorySeparatorChar)
            {
                workingPath = workingPath.Remove(0, 1);
            }

            // This will include the extension, if there is one
            var fileName = Path.GetFileName(path);

            // This is a file, so we need to strip out the filename
            // from the path we're looking up to ensure that we find
            // the right parent directory            
            workingPath = workingPath.Remove(workingPath.Length - fileName.Length, fileName.Length);

            // Trailing path separators will stop us from finding the path correctly
            if (workingPath.EndsWith(Path.DirectorySeparatorChar))
            {
                workingPath = workingPath.TrimEnd(new char[] { Path.DirectorySeparatorChar });
            }

            var current = this._rootNode;
            var components = workingPath.Split(Path.DirectorySeparatorChar);
            foreach (string component in components)
            {
                if (String.IsNullOrEmpty(component))
                {
                    continue;
                }

                // If any part of the path isn't found in the
                // dictionaries, then it's not going to be there (and
                // by definition, nor will any files)
                if (!current.Directories.TryGetValue(component, out current))
                {
                    return false;
                }
            }

            // Since we're looking a file, we can assume that the
            // dictionary looking up will give us the conclusive
            // answer (since we found the folder path already)
            return current.Files.ContainsKey(fileName);
        }

        private void AddFileToHashOrQueueForHashing(FileNode file)
        {
            if (file.Hash == null)
            {
                this._itemsRequiringHashing.Enqueue(file);
                return;
            }

            if (!this._hashToDuplicates.TryGetValue(file.Hash, out Duplicates duplicates))
            {
                duplicates = new Duplicates() { FirstFile = file };
                this._hashToDuplicates.Add(file.Hash, duplicates);
                return;
            }

            duplicates.DuplicateFiles.Add(file);
        }

        #region State Saving
        private static void AddFilesIntoSavedState(ICollection<DirectoryNode> directories, ICollection<FileNode> files, XmlElement parent)
        {
            foreach (var dn in directories)
            {
                var dirElement = parent.OwnerDocument.CreateElement("Folder");

                Program.AddFilesIntoSavedState(dn.Directories.Values, dn.Files.Values, dirElement);

                // If we have a directory with no children
                // then there is no point in persisting this into
                // our state state
                if (dirElement.ChildNodes.Count == 0)
                {
                    continue;
                }

                dirElement.SetAttribute("Name", dn.Name);
                parent.AppendChild(dirElement);
            }

            foreach (var file in files)
            {
                var fileElement = parent.OwnerDocument.CreateElement("File");
                fileElement.SetAttribute("Name", file.Name);

                var hash = file.HashAsString;
                if (!String.IsNullOrEmpty(hash))
                {
                    fileElement.AppendChild(parent.OwnerDocument.CreateTextNode(hash));
                }

                parent.AppendChild(fileElement);
            }
        }
        #endregion State Saving

        #region State Loading
        private void LoadState(string path)
        {
            var state = new XmlDocument();
            state.Load(path);

            var rootOfState = state.DocumentElement as XmlElement;

            var root = new DirectoryNode(String.Empty, null);
            this.ProcessNodes(root, rootOfState.ChildNodes);

            this._rootNode = root;
        }

        private void ProcessNodes(DirectoryNode parent, XmlNodeList children)
        {
            foreach (XmlNode n in children)
            {
                XmlElement item = n as XmlElement;

                switch (item.Name)
                {
                    case "Folder":
                        var newFolder = new DirectoryNode(item.GetAttribute("Name"), parent);
                        this.ProcessNodes(newFolder, item.ChildNodes);
                        parent.Directories[newFolder.Name] = newFolder;
                        break;

                    case "File":
                        var fileName = item.GetAttribute("Name");
                        var newFile = new FileNode(fileName, parent);

                        if (item.FirstChild != null && item.FirstChild.NodeType == XmlNodeType.Text)
                        {
                            // Assume we have the hash, so convert the child text to the byte[]
                            var hash = Program.GetHashBytesFromString(item.FirstChild.InnerText);
                            newFile.Hash = hash;
                        }

                        parent.Files[fileName] = newFile;

                        this.AddFileToHashOrQueueForHashing(newFile);
                        break;
                }
            }
        }
        #endregion State Loading

        #region Utility
        private static void UpdateConsole(string message, string data)
        {
            Console.SetCursorPosition(0, Console.CursorTop);

            // If the output is too large to fit on one line, lets
            // trim the *Beginning* of the data so we see the end
            // e.g. to see the filename rather than some repeated
            // part of a file path
            var totalLength = message.Length + data.Length;
            if (totalLength > Console.BufferWidth)
            {
                var excess = totalLength - Console.BufferWidth;
                if (excess < data.Length)
                {
                    data = data.Remove(0, excess);
                }
            }

            if(totalLength < Console.BufferWidth)
            {
                // Pad the rest of the data with spaces
                data = data + new String(' ', Console.BufferWidth - totalLength);
            }

            Console.Write(message, data);
        }

        private static void PrintUsage()
        {
            Program.PrintHeader();

            Console.WriteLine("Usage");
            Console.WriteLine("=====");
            Console.WriteLine("dotnet FileDeduper.dll /root <path to find duplicates in>");
            Console.WriteLine();
            Console.WriteLine("Required:");
            Console.WriteLine("/r[oot]:   The root path where to start this search from.");
            Console.WriteLine();
            Console.WriteLine("Optional:");
            Console.WriteLine("/res[ume]: Loads the state file, and continues from where it was. This will check the file system for new files");
            Console.WriteLine("/st[ate]:  File path for state to be saved. If not specified, saves 'State.xml' in the working directory");
            Console.WriteLine("/skip:     Skips checking the file system and only uses the saved state to determine work");
            Console.WriteLine("/d[estinationroot]: Full path to a directory root to exclude");

        }

        private static void PrintHeader()
        {
            Console.WriteLine("FileDeduper -- Scans a file tree and lists any idenitical files");
            Console.WriteLine("Copyright 2016, Dominic Hopton");
            Console.WriteLine();
        }

        private static string GetPathForDirectory(DirectoryNode dn)
        {
            var components = new List<string>();

            while (dn != null && !String.IsNullOrEmpty(dn.Name))
            {
                components.Insert(0, dn.Name);
                dn = dn.Parent;
            }

            return String.Join(Path.DirectorySeparatorChar, components);
        }

        private static byte[] GetHashBytesFromString(string innerText)
        {
            Debug.Assert(innerText.Length == 32, "Hash is not the correct length");
            var bytes = new List<byte>(16);

            // Stride over the two chars at a time (two chars = 1 hex byte)
            for (var i = 0; i < innerText.Length; i += 2)
            {
                var byteAsHex = innerText.Substring(i, 2);
                Byte.TryParse(byteAsHex, NumberStyles.HexNumber, null, out byte parsedValue);
                bytes.Add(parsedValue);
            }

            return bytes.ToArray();
        }
        #endregion Utility
    }
}
