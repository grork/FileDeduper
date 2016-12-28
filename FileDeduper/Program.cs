using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace Codevoid.Utility.FileDeduper
{
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

            Stack<string> files = new Stack<string>();
            Stack<string> directories = new Stack<string>();
            directories.Push(this._root.FullName);

            while (directories.Count > 0)
            {
                var directory = directories.Pop();

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(directory);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var childDir in childDirectories)
                {
                    try
                    {
                        directories.Push(childDir);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                }

                IEnumerable<string> childFiles;
                try
                {
                    childFiles = Directory.EnumerateFiles(directory);
                }
                catch (SecurityException)
                {
                    continue;
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                foreach(var childFile in childFiles)
                {
                    files.Push(childFile);
                }

                this.UpdateConsole(files.Count);
            }

            Console.WriteLine("Duration: {0}", DateTime.Now - start);
        }

        private void UpdateConsole(int count)
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
