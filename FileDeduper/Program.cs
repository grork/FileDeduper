using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Codevoid.Utility.FileDeduper
{
    struct DirectoryToProcess
    {
        public string Path;
        public DirectoryNode Parent;
    }

    class DirectoryNode
    {
        private string _name;
        private Dictionary<string, string> _files;
        private Dictionary<string, DirectoryNode> _directories;

        internal DirectoryNode(string name)
        {
            this._name = name;
            this._files = new Dictionary<string, string>();
            this._directories = new Dictionary<string, DirectoryNode>();
        }

        public string Name
        {
            get
            {
                return this._name;
            }
        }

        public IDictionary<string, string> Files
        {
            get { return this._files; }
        }

        public IDictionary<string, DirectoryNode> Directories
        {
            get { return this._directories; }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            var app = new Program();
            bool parsedArgs = app.ParseArgs(args);
            if (!parsedArgs)
            {
                Program.PrintUsage();
                return;
            }

            Console.CursorVisible = false;

            app.Begin();
        }

        private string _root;
        private DirectoryNode _rootNode;
        private bool _resume;
        private string _statePath = "state.xml";

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


                    default:
                        break;
                }
            }

            return true;
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
            }
            else
            {
                if(this._resume)
                {
                    Console.WriteLine("State File not found, loading from FileSystem");
                }

                this._rootNode = Program.LoadStateFromFileSystem(this._root);
            }

            Console.WriteLine();
            Console.WriteLine("Duration: {0}", DateTime.Now - start);

            // Validate the state against the file system.
            var directories = new Queue<DirectoryToProcess>();
            directories.Enqueue(new DirectoryToProcess() { Path = this._root });

            while (directories.Count > 0)
            {
                var directory = directories.Dequeue();

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(directory.Path);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                foreach (var childDir in childDirectories)
                {
                    directories.Enqueue(new DirectoryToProcess() { Path = childDir });
                }

                IEnumerable<string> childFiles = Directory.EnumerateFiles(directory.Path);

                foreach (var childFile in childFiles)
                {
                    if (!this.FileExistsInLoadedState(childFile))
                    {
                        throw new FileNotFoundException();
                    }
                }
            }

            // Write the loaded data to disk
            XmlDocument state = new XmlDocument();
            var rootOfState = state.CreateElement("State");
            state.AppendChild(rootOfState);

            Program.AddChildrenToNode(this._rootNode.Directories.Values, this._rootNode.Files.Values, rootOfState);

            state.Save(this._statePath);
        }

        private static DirectoryNode LoadStateFromFileSystem(string rootPath)
        {
            DirectoryNode root = null;
            // Populate from the File System
            Queue<DirectoryToProcess> directories = new Queue<DirectoryToProcess>();
            directories.Enqueue(new DirectoryToProcess() { Path = rootPath });

            ulong fileCount = 0;

            while (directories.Count > 0)
            {
                var directory = directories.Dequeue();

                var current = new DirectoryNode(Path.GetFileName(directory.Path));
                if (root == null)
                {
                    root = current;
                }

                if (directory.Parent != null)
                {
                    directory.Parent.Directories[current.Name] = current;
                }

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(directory.Path);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                foreach (var childDir in childDirectories)
                {
                    directories.Enqueue(new DirectoryToProcess() { Path = childDir, Parent = current });
                }

                IEnumerable<string> childFiles = Directory.EnumerateFiles(directory.Path);

                foreach (var childFile in childFiles)
                {
                    var fileName = Path.GetFileName(childFile);
                    current.Files[fileName] = fileName;
                    fileCount++;
                }

                Program.UpdateConsole(fileCount);
            }

            return root;
        }

        private bool FileExistsInLoadedState(string path)
        {
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

            // If this is a file, we need to strip out the filename
            // from the path we're looking up to ensure that we find
            // the right parent directory            
            string fileName = null;
            // This will include the extension, if there is one
            fileName = Path.GetFileName(path);

            // Remove the file name from the working path (E.g.
            // leaving only the directory)
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
                // dictionaries, then it's not doing to be there (and
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
        private static void AddChildrenToNode(ICollection<DirectoryNode> directories, ICollection<string> files, XmlElement parent)
        {
            foreach (var dn in directories)
            {
                var dirElement = parent.OwnerDocument.CreateElement("Folder");

                Program.AddChildrenToNode(dn.Directories.Values, dn.Files.Values, dirElement);

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
                fileElement.SetAttribute("Name", file);
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

            DirectoryNode root = new DirectoryNode("");
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
                        var newFolder = new DirectoryNode(item.GetAttribute("Name"));
                        Program.ProcessNodes(newFolder, item.ChildNodes);
                        parent.Directories[newFolder.Name] = newFolder;
                        break;

                    case "File":
                        var fileName = item.GetAttribute("Name");
                        parent.Files[fileName] = fileName;
                        break;
                }
            }
        }
        #endregion State Loading

        #region Utility
        private static void UpdateConsole(ulong count)
        {
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(count);
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
        #endregion Utility
    }
}
