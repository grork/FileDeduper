using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Codevoid.Utility.FileDeduper
{
    class DirectoryNode
    {
        private string _name;
        private Dictionary<string, FileNode> _files;
        private Dictionary<string, DirectoryNode> _directories;
        private DirectoryNode _parent;

        internal DirectoryNode(string name, DirectoryNode parent)
        {
            this._name = name;
            this._files = new Dictionary<string, FileNode>();
            this._directories = new Dictionary<string, DirectoryNode>();
            this._parent = parent;
        }

        internal string Name
        { get { return this._name; } }

        internal IDictionary<string, FileNode> Files
        { get { return this._files; } }

        internal IDictionary<string, DirectoryNode> Directories
        { get { return this._directories; } }

        internal DirectoryNode Parent
        {
            get { return this._parent; }
        }
    }

    class FileNode
    {
        private string _name;
        private DirectoryNode _parent;
        private string _hashAsString;
        private byte[] _hash;

        internal FileNode(string name, DirectoryNode parent)
        {
            this._name = name;
            this._parent = parent;
        }

        internal string Name
        { get { return this._name; } }

        internal DirectoryNode Parent
        { get { return this._parent; } }

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

                if(this._hashAsString == null)
                {
                    this._hashAsString = BitConverter.ToString(this._hash).Replace("-", "");
                }

                return this._hashAsString;
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var app = new Program();
            bool parsedArgs = app.ParseArgs(args);
            if (!parsedArgs)
            {
                Program.PrintUsage();
                return;
            }

            // Reduce flicker when we update the processed file count
            Console.CursorVisible = false;

            app.ListenForCancellation();
            app.Begin();
        }

        private string _root;
        private DirectoryNode _rootNode;
        private bool _resume;
        private string _statePath = "state.xml";
        private bool _skipFileSystemCheck;
        private bool _wasCancelled;
        private Queue<FileNode> _itemsRequiringHashing = new Queue<FileNode>(5000);

        bool ParseArgs(string[] args)
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
                        this._root = @"\\?\" + args[argIndex];
                        break;

                    case "/res":
                    case "/resume":
                        this._resume = true;
                        break;

                    case "/sf":
                    case "/state":
                        argIndex++;
                        this._statePath = args[argIndex];
                        break;

                    case "/skip":
                        this._skipFileSystemCheck = true;
                        break;

                    default:
                        break;
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

        void Begin()
        {
            Program.PrintHeader();

            Console.WriteLine("Root: {0}", this._root);

            DateTime start = DateTime.Now;
            Console.WriteLine("Started At: {0}", start);

            if (this._resume && File.Exists(this._statePath))
            {
                Console.WriteLine("Loading Saved State");
                this._rootNode = Program.LoadState(this._statePath);
                Console.WriteLine("State Loaded In: {0}", DateTime.Now - start);
            }
            else if (this._resume)
            {
                Console.WriteLine("State File not found, loading information from the file system");
            }

            if (this._rootNode == null)
            {
                this._rootNode = new DirectoryNode(String.Empty, null);
            }

            ulong addedFileCount = 0;

            if (!this._skipFileSystemCheck)
            {
                // Validate the state against the file system.
                var directories = new Queue<string>();
                directories.Enqueue(this._root);
                bool cancelled = false;

                while (directories.Count > 0)
                {
                    var directory = directories.Dequeue();

                    // Scavange all the directories we need to look at
                    // and add them to the queue to be processed
                    IEnumerable<string> childDirectories;
                    try
                    {
                        childDirectories = Directory.EnumerateDirectories(directory);
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

                    IEnumerable<string> childFiles = Directory.EnumerateFiles(directory);

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

                        if (!this.FileExistsInLoadedState(childFile))
                        {
                            // File needs to added to the tree, so process it
                            this.AddFileToLoadedState(childFile);
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
                        Program.UpdateConsole("New Files added: {0}", addedFileCount);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("State Validated in: {0}", DateTime.Now - start);
            }

            if (addedFileCount > 0)
            {
                // Write the loaded data to disk
                XmlDocument state = new XmlDocument();
                var rootOfState = state.CreateElement("State");
                rootOfState.SetAttribute("GeneratedAt", DateTime.Now.ToString());
                state.AppendChild(rootOfState);

                Program.AddFilesIntoSavedState(this._rootNode.Directories.Values, this._rootNode.Files.Values, rootOfState);

                state.Save(this._statePath);
            }
        }

        private void AddFileToLoadedState(string path)
        {
            var workingPath = path.Remove(0, this._root.Length);

            if (String.IsNullOrEmpty(workingPath))
            {
                // This can happen for the root path
                return;
            }

            // Remove any leading \'s from the working path
            if (workingPath[0] == '\\')
            {
                workingPath = workingPath.Remove(0, 1);
            }

            string fileName = null;
            // This will include the extension, if there is one
            fileName = Path.GetFileName(path);

            // This is a file, so we need to strip out the filename
            // from the path we're looking up to ensure that we find
            // the right parent directory            
            workingPath = workingPath.Remove(workingPath.Length - fileName.Length, fileName.Length);

            // Trailing \'s will stop us from finding the path correctly
            if (workingPath.EndsWith("\\"))
            {
                workingPath = workingPath.TrimEnd(new char[] { '\\' });
            }

            // Start our search at the root
            var current = this._rootNode;

            // Break out the path into the individual folder parts
            string[] components = workingPath.Split('\\');
            foreach (string component in components)
            {
                if (String.IsNullOrEmpty(component))
                {
                    continue;
                }

                DirectoryNode directory = null;

                // If any part of the path isn't found in the
                // dictionaries, we need to fill in the missing parts
                if (!current.Directories.TryGetValue(component, out directory))
                {
                    directory = new DirectoryNode(component, current);
                    current.Directories[component] = directory;
                }

                current = directory;
            }

            // Since we're looking a file, we can assume that the
            // dictionary looking up will give us the conclusive
            // answer (since we found the folder path already)
            current.Files[fileName] = new FileNode(fileName, current);
        }

        private bool FileExistsInLoadedState(string path)
        {
            if (this._rootNode == null)
            {
                return false;
            }

            var workingPath = path.Remove(0, this._root.Length);

            if (String.IsNullOrEmpty(workingPath))
            {
                // This is the root node, by definition
                return true;
            }

            // Remove any leading \'s from the working path
            if (workingPath[0] == '\\')
            {
                workingPath = workingPath.Remove(0, 1);
            }

            string fileName = null;
            // This will include the extension, if there is one
            fileName = Path.GetFileName(path);

            // This is a file, so we need to strip out the filename
            // from the path we're looking up to ensure that we find
            // the right parent directory            
            workingPath = workingPath.Remove(workingPath.Length - fileName.Length, fileName.Length);

            // Trailing \'s will stop us from finding the path correctly
            if (workingPath.EndsWith("\\"))
            {
                workingPath = workingPath.TrimEnd(new char[] { '\\' });
            }

            var current = this._rootNode;
            string[] components = workingPath.Split('\\');
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
                if(!String.IsNullOrEmpty(hash))
                {
                    fileElement.AppendChild(parent.OwnerDocument.CreateTextNode(hash));
                }

                parent.AppendChild(fileElement);
            }
        }
        #endregion State Saving

        #region State Loading
        private static DirectoryNode LoadState(string path)
        {
            XmlDocument state = new XmlDocument();
            state.Load(path);

            XmlElement rootOfState = state.DocumentElement as XmlElement;

            DirectoryNode root = new DirectoryNode(String.Empty, null);
            Program.ProcessNodes(root, rootOfState.ChildNodes);

            return root;
        }

        private static void ProcessNodes(DirectoryNode parent, XmlNodeList children)
        {
            foreach (XmlNode n in children)
            {
                XmlElement item = n as XmlElement;

                switch (item.Name)
                {
                    case "Folder":
                        var newFolder = new DirectoryNode(item.GetAttribute("Name"), parent);
                        Program.ProcessNodes(newFolder, item.ChildNodes);
                        parent.Directories[newFolder.Name] = newFolder;
                        break;

                    case "File":
                        var fileName = item.GetAttribute("Name");
                        var newFile = new FileNode(fileName, parent);
                        
                        if(item.FirstChild != null && item.FirstChild.NodeType == XmlNodeType.Text)
                        {
                            // Assume we have the hash, so convert the child text to the byte[]
                            byte[] hash = Program.GetHashBytesFromString(item.FirstChild.InnerText);
                            newFile.Hash = hash;
                        }

                        parent.Files[fileName] = newFile;
                        break;
                }
            }
        }
        #endregion State Loading

        #region Utility
        private static void UpdateConsole(string message, ulong count)
        {
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(message, count);
        }

        private static void PrintUsage()
        {
            Program.PrintHeader();

            Console.WriteLine("Arguments:");
            Console.WriteLine("/root: The root path where to start this search from.");
        }

        private static void PrintHeader()
        {
            Console.WriteLine("FileDeduper -- Scans a file tree and lists any idenitical files");
            Console.WriteLine("Copyright 2016, Dominic Hopton");
            Console.WriteLine();
        }

        private static string GetPathForDirectory(DirectoryNode dn)
        {
            List<string> components = new List<string>();

            while(dn != null)
            {
                components.Insert(0, dn.Name);
                dn = dn.Parent;
            }

            return String.Join("\\", components);
        }

        private static byte[] GetHashBytesFromString(string innerText)
        {
            Debug.Assert(innerText.Length == 40, "Hash is not the correct length");
            List<byte> bytes = new List<byte>(20);

            // Stride over the two chars at a time (two chars = 1 hex byte)
            for(var i = 0; i < 40; i +=2)
            {
                string byteAsHex = innerText.Substring(i, 2);
                byte parsedValue;
                Byte.TryParse(byteAsHex, NumberStyles.HexNumber, null, out parsedValue);
                bytes.Add(parsedValue);
            }

            return bytes.ToArray();
        }
        #endregion Utility
    }
}
