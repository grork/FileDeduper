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
        private List<string> _files;
        private Dictionary<string, DirectoryNode> _directories;

        internal DirectoryNode(string name)
        {
            this._name = name;
            this._files = new List<string>();
            this._directories = new Dictionary<string, DirectoryNode>();
        }

        public string Name
        {
            get
            {
                return this._name;
            }
        }

        public IList<string> Files
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
                if(root == null)
                {
                    root = current;
                }

                if(directory.Parent != null)
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

                foreach(var childFile in childFiles)
                {
                    current.Files.Add(Path.GetFileName(childFile));
                    fileCount++;
                }

                this.UpdateConsole(fileCount);
            }

            Console.WriteLine();
            Console.WriteLine("Duration: {0}", DateTime.Now - start);

            XmlDocument state = new XmlDocument();
            var rootOfState = state.CreateElement("State");
            state.AppendChild(rootOfState);

            var discoveredFiles = state.CreateElement("DiscoveredFiles");
            rootOfState.AppendChild(discoveredFiles);

            this.AddChildrenToNode(root.Directories.Values, root.Files, discoveredFiles);

            state.Save("State.xml");

            Console.ReadLine();
        }

        private void AddChildrenToNode(ICollection<DirectoryNode> directories, IList<string> files, XmlElement parent)
        {
            foreach (var dn in directories)
            {
                var dirElement = parent.OwnerDocument.CreateElement("Folder");

                this.AddChildrenToNode(dn.Directories.Values, dn.Files, dirElement);

                if (dirElement.ChildNodes.Count > 0)
                {
                    dirElement.SetAttribute("Name", dn.Name);
                    parent.AppendChild(dirElement);
                }
            }

            foreach(var file in files)
            {
                var fileElement = parent.OwnerDocument.CreateElement("File");
                fileElement.SetAttribute("Name", file);
                parent.AppendChild(fileElement);
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
