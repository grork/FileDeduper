using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;

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
        private List<DirectoryNode> _directories;

        internal DirectoryNode(string name)
        {
            this._name = name;
            this._files = new List<string>();
            this._directories = new List<DirectoryNode>();
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

        public IList<DirectoryNode> Directories
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
            Console.ReadLine();
        }

        private DirectoryInfo _root;

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
                        this._root = new DirectoryInfo(@"\\?\" + args[argIndex]);
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

            Console.WriteLine("Root: {0}", this._root.FullName);

            DateTime start = DateTime.Now;
            Console.WriteLine("Started At: {0}", start);

            Queue<DirectoryToProcess> directories = new Queue<DirectoryToProcess>();
            directories.Enqueue(new DirectoryToProcess() { Path = this._root.FullName });

            DirectoryNode root = null;
            ulong fileCount = 0;

            while (directories.Count > 0)
            {
                var directory = directories.Dequeue();

                var current = new DirectoryNode(Path.GetDirectoryName(directory.Path));
                if(root == null)
                {
                    root = current;
                }

                if(directory.Parent != null)
                {
                    directory.Parent.Directories.Add(current);
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

            ulong countedCharacters = 0;
            Queue<DirectoryNode> foundDirectories = new Queue<DirectoryNode>();
            foundDirectories.Enqueue(root);

            while(foundDirectories.Count > 0)
            {
                var dir = foundDirectories.Dequeue();
                foreach(var node in dir.Directories)
                {
                    foundDirectories.Enqueue(node);
                }

                foreach(var file in dir.Files)
                {
                    countedCharacters += (ulong)file.Length;
                }
            }

            Console.WriteLine("Counted Chars: {0}", countedCharacters);
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
