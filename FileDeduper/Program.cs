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

            Queue<DirectoryToProcess> directories = new Queue<DirectoryToProcess>();
            directories.Enqueue(new DirectoryToProcess() { Path = this._root });

            DirectoryNode root = null;
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

                this.UpdateConsole(fileCount);
            }

            Console.WriteLine();
            Console.WriteLine("Duration: {0}", DateTime.Now - start);

            XmlDocument state = new XmlDocument();
            var rootOfState = state.CreateElement("State");
            state.AppendChild(rootOfState);

            this.AddChildrenToNode(root.Directories.Values, root.Files.Values, rootOfState);

            state.Save("State.xml");

            Console.ReadLine();

            directories = new Queue<DirectoryToProcess>();
            directories.Enqueue(new DirectoryToProcess() { Path = this._root });

            while (directories.Count > 0)
            {
                var directory = directories.Dequeue();

                if(!this.DirectoryExistsInCache(root, directory.Path, false))
                {
                    throw new FileNotFoundException();
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
                    directories.Enqueue(new DirectoryToProcess() { Path = childDir });
                }

                IEnumerable<string> childFiles = Directory.EnumerateFiles(directory.Path);

                foreach (var childFile in childFiles)
                {
                    this.DirectoryExistsInCache(root, childFile, true);
                }
            }
        }

        private bool DirectoryExistsInCache(DirectoryNode root, string path, bool isFile)
        {
            var workingPath = path.Remove(0, this._root.Length);
            
            if(String.IsNullOrEmpty(workingPath))
            {
                // This is the root node, by definition
                return true;
            }

            if (workingPath[0] == '\\')
            {
                workingPath = workingPath.Remove(0, 1);
            }

            string fileName = null;
            if(isFile)
            {
                fileName = Path.GetFileName(path);
                workingPath = workingPath.Replace(fileName, "");

                if (workingPath.EndsWith("\\"))
                {
                    workingPath = workingPath.TrimEnd(new char[] { '\\' });
                }
            }

            var current = root;
            string[] components = workingPath.Split('\\');
            foreach(string component in components)
            {
                if(!current.Directories.TryGetValue(component, out current))
                {
                    return false;
                }
            }

            if(isFile)
            {
                return current.Files.ContainsKey(fileName);
            }

            return true;
        }

        private void AddChildrenToNode(ICollection<DirectoryNode> directories, ICollection<string> files, XmlElement parent)
        {
            foreach (var dn in directories)
            {
                var dirElement = parent.OwnerDocument.CreateElement("Folder");

                this.AddChildrenToNode(dn.Directories.Values, dn.Files.Values, dirElement);
                dirElement.SetAttribute("Name", dn.Name);
                parent.AppendChild(dirElement);
            }

            foreach(var file in files)
            {
                var fileElement = parent.OwnerDocument.CreateElement("File");
                fileElement.SetAttribute("Name", file);
                parent.AppendChild(fileElement);
            }
        }

        private DirectoryNode LoadState()
        {
            XmlDocument state = new XmlDocument();
            state.Load("State.xml");

            XmlElement rootOfState = state.DocumentElement as XmlElement;

            DirectoryNode root = new DirectoryNode("");
            this.ProcessNodes(root, rootOfState.ChildNodes);

            return root;
        }

        private void ProcessNodes(DirectoryNode parent, XmlNodeList children)
        {
            foreach (XmlNode n in children)
            {
                XmlElement item = n as XmlElement;

                switch (item.Name)
                {
                    case "Folder":
                        var newFolder = new DirectoryNode(item.GetAttribute("Name"));
                        this.ProcessNodes(newFolder, item.ChildNodes);
                        parent.Directories[newFolder.Name] = newFolder;
                        break;

                    case "File":
                        var fileName = item.GetAttribute("Name");
                        parent.Files[fileName] = fileName;
                        break;
                }
            }
        }

        private void UpdateConsole(ulong count)
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
    }
}
