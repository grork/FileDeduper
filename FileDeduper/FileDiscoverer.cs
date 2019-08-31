using System;
using System.Collections.Generic;
using System.IO;

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
        internal string FullPath { get; }
        internal DirectoryNode Parent { get; }
        internal bool FromOriginalsTree { get; }
        private string _hashAsString;
        private byte[] _hash;

        internal FileNode(string name, string fullPath, DirectoryNode parent, bool sourcedFromOriginals)
        {
            this.Name = name;
            this.FullPath = fullPath;
            this.Parent = parent;
            this.FromOriginalsTree = sourcedFromOriginals;
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

    class FileDiscoverer
    {
        private DirectoryInfo _root;
        private DirectoryInfo _duplicatesDestinationRoot;
        private bool _sourcedFromOriginals = false;
        
        internal DirectoryNode RootNode { get; private set; }
        internal bool Cancelled = false;
        internal ulong DiscoveredFileCount { get; private set; }

        internal event EventHandler<FileNode> FileDiscovered;

        internal FileDiscoverer(DirectoryInfo root, DirectoryInfo duplicatesDestinationRoot, bool sourcedFromOriginals = false)
        {
            this._root = root;
            this._duplicatesDestinationRoot = duplicatesDestinationRoot;
            this._sourcedFromOriginals = sourcedFromOriginals;
            this.RootNode = new DirectoryNode(String.Empty, null);
        }

        internal void DiscoverFiles()
        {
            // Validate the state against the file system.
            var directories = new Queue<DirectoryInfo>();
            directories.Enqueue(this._root);

            while (directories.Count > 0)
            {
                lock (this)
                {
                    if (this.Cancelled)
                    {
                        break;
                    }
                }

                var directory = directories.Dequeue();

                // If the directory is somewhere under our destination for moving
                // then exclude that directory.
                if (this._duplicatesDestinationRoot != null && directory.FullName.StartsWith(this._duplicatesDestinationRoot.FullName))
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
                catch (DirectoryNotFoundException)
                {
                    Console.WriteLine("Unable to find folder '${0}'", directory.FullName);
                    continue;
                }

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
                        if (this.Cancelled)
                        {
                            break;
                        }
                    }

                    if (!this.FileExistsInLoadedState(childFile.FullName))
                    {
                        // File needs to added to the tree, so process it
                        this.AddFileToLoadedState(childFile.FullName);
                        this.DiscoveredFileCount++;
                    }
                }
            }
        }

        private bool FileExistsInLoadedState(string path)
        {
            if (this.RootNode.Files.Count == 0 && this.RootNode.Directories.Count == 0)
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

            var current = this.RootNode;
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
            var current = this.RootNode;

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
            var newFile = new FileNode(fileName, path, current, this._sourcedFromOriginals);
            current.Files[fileName] = newFile;
            
            EventHandler<FileNode> handler = this.FileDiscovered;
            if(handler != null)
            {
                handler(this, newFile);
            }
        }
    }
}