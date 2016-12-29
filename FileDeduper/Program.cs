﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Codevoid.Utility.FileDeduper
{
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
        { get { return this._name; } }

        public IDictionary<string, string> Files
        { get { return this._files; } }

        public IDictionary<string, DirectoryNode> Directories
        { get { return this._directories; } }
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
                Console.WriteLine("State Loaded In: {0}", DateTime.Now - start);
            }
            else if (this._resume)
            {
                Console.WriteLine("State File not found, loading information from the file system");
            }

            if(this._rootNode == null)
            {
                this._rootNode = new DirectoryNode(String.Empty);
            }

            // Validate the state against the file system.
            var directories = new Queue<string>();
            directories.Enqueue(this._root);

            ulong addedFileCount = 0;

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
                    if (!this.FileExistsInLoadedState(childFile))
                    {
                        // File needs to added to the tree, so process it
                        this.AddFileToLoadedState(childFile);
                        addedFileCount++;
                    }
                }

                if (addedFileCount > 0)
                {
                    Program.UpdateConsole("New Files added: {0}", addedFileCount);
                }
            }

            Console.WriteLine();
            Console.WriteLine("State Validated in: {0}", DateTime.Now - start);

            if (addedFileCount > 0)
            {
                // Write the loaded data to disk
                XmlDocument state = new XmlDocument();
                var rootOfState = state.CreateElement("State");
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
                    directory = new DirectoryNode(component);
                    current.Directories[component] = directory;
                }

                current = directory;
            }

            // Since we're looking a file, we can assume that the
            // dictionary looking up will give us the conclusive
            // answer (since we found the folder path already)
            current.Files[fileName] = fileName;
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
        private static void AddFilesIntoSavedState(ICollection<DirectoryNode> directories, ICollection<string> files, XmlElement parent)
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
        #endregion Utility
    }
}
