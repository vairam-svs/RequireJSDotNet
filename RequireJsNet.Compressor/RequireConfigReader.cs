﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RequireJsNet.Compressor
{
    internal class RequireConfigReader
    {
        
        private const string ConfigFileName = "RequireJS.config";
        private const string DefaultScriptDirectory = "Scripts";
        private RequireConfiguration Configuration { get; set; }
        private string ProjectPath { get; set; }
        private List<string> FilePaths { get; set; }

        public RequireConfigReader(string projectPath, List<string> filePaths)
        {
            ProjectPath = projectPath;
            FilePaths = filePaths;
            Configuration = new RequireConfiguration
            {
                EntryPoint = Path.Combine(projectPath + Path.DirectorySeparatorChar, DefaultScriptDirectory)
            };
        }

        public List<Bundle> ParseConfigs()
        {

            if (!Directory.Exists(ProjectPath))
            {
                throw new DirectoryNotFoundException("Could not find project directory.");
            }

            FindConfigs();


            foreach (var filePath in FilePaths)
            {
                LoadConfigData(filePath);
            }

            ResolvePhysicalPaths();
            ResolveBundleIncludes();

            var bundles = new List<Bundle>();
            foreach (var bundleDefinition in Configuration.Bundles.Where(r => !r.IsVirtual))
            {
                var bundle = new Bundle();
                bundle.Output = GetOutputPath(bundleDefinition);
                bundle.Files = bundleDefinition.Items
                                                .Select(r => new FileSpec(r.PhysicalPath, r.CompressionType))
                                                .ToList();
                bundles.Add(bundle);
            }

            return bundles;
        }


        private void ResolveBundleIncludes()
        {
            var rootBundles = Configuration.Bundles.Where(r => !r.Includes.Any()).ToList();
            if (!rootBundles.Any())
            {
                throw new Exception("Could not find any bundle with no dependency. Check your config for cyclic dependencies.");
            }
            rootBundles.ForEach(r => r.ParsedIncludes = true);
            var maxIterations = 500;
            var currentIt = 0;
            while (Configuration.Bundles.Where(r => !r.ParsedIncludes).Any())
            {
                // shouldn't really happen, but we'll use this as a safeguard against an endless loop for the moment
                if (currentIt > maxIterations)
                {
                    throw new Exception("Maximum number of iterations exceeded. Check your config for cyclick dependencies");
                }

                // get all the bundles that have parents with resolved dependencies and haven't been resolved themselves
                var parsableBundles = GetBundlesWithResolvedParents();
                // we've checked earlier if there are any bundles that haven't been parsed
                // if there are bundles that haven't been parsed but there aren't any we can parse, something went wrong
                if (!parsableBundles.Any())
                {
                    throw new Exception("Could not parse bundle includes. Check your config for cyclic dependencies.");
                }
                
                foreach (var bundle in parsableBundles)
                {
                    // store a reference to the old list
                    var oldItemList = bundle.Items;
                    // instantiate a new one so that when we're done we can append the old scripts
                    bundle.Items = new List<BundleItem>();
                    var parents = bundle.Includes.Select(r => GetBundleByName(r)).ToList();
                    foreach (var parent in parents)
                    {
                        bundle.Items.AddRange(parent.Items);
                    }
                    bundle.Items.AddRange(oldItemList);
                    bundle.Items = bundle.Items.GroupBy(r => r.PhysicalPath).Select(r => r.FirstOrDefault()).ToList();
                    bundle.ParsedIncludes = true;
                }
                currentIt++;
            }

        }

        private BundleDefinition GetBundleByName(string name)
        {
            var result =  Configuration.Bundles.Where(r => r.Name == name).FirstOrDefault();
            if (result == null)
            {
                throw new Exception("Could not find bundle with name " + name);
            }
            return result;
        }

        private List<BundleDefinition> GetBundlesWithResolvedParents()
        {
            var allBundles = Configuration.Bundles;
            var result = new List<BundleDefinition>();
            foreach (var bundle in allBundles.Where(r => !r.ParsedIncludes))
            {
                // for each include, get its bundle.ParsedIncludes property
                // select those that don't have their parents resolved
                // if any such items exist, it means that the item's parents haven't been resolved
                var parentsResolved = !(bundle.Includes

                                        .Select(r => GetBundleByName(r).ParsedIncludes)
                        
                        .Where(r => !r)
                        .Any());
                if (parentsResolved)
                {
                    result.Add(bundle);
                }
            }
            return result;
        }

        private void ResolvePhysicalPaths()
        {
            foreach (var item in Configuration.Bundles.SelectMany(r => r.Items))
            {
                var finalName = item.ModuleName;

                // this will only go 1 level deep, other cases should be taken into account
                if (Configuration.Paths.ContainsKey(finalName))
                {
                    finalName = Configuration.Paths[finalName];
                }
                item.PhysicalPath = Path.Combine(ProjectPath, Configuration.EntryPoint, finalName + ".js");
                if (!File.Exists(item.PhysicalPath))
                {
                    throw new FileNotFoundException("Could not load script" + item.PhysicalPath, item.PhysicalPath);
                }
            }
        }

        private string GetOutputPath(BundleDefinition bundle)
        {
            if (string.IsNullOrEmpty(bundle.OutputPath))
            {
                return Path.Combine(ProjectPath, Configuration.EntryPoint, bundle.Name + ".js");
            }
            var directory = Path.GetDirectoryName(bundle.OutputPath) ?? "";
            var fileName = Path.GetFileName(bundle.OutputPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = bundle.Name + ".js";
            }
            return Path.Combine(ProjectPath, Configuration.EntryPoint, directory, fileName); 
        }

        private void FindConfigs()
        {
            if (FilePaths.Any())
            {
                return;
            }
            var files = Directory.GetFiles(ProjectPath, ConfigFileName);
            foreach (var file in files)
            {
                FilePaths.Add(file);
            }
            if (!FilePaths.Any())
            {
                throw new ArgumentException("No Require config files were provided and none were found in the project directory.");    
            }
            
        }

        private void LoadConfigData(string path)
        {
            var doc = XDocument.Load(path);
            if (doc == null || doc.Document == null || doc.Document.Root == null)
            {
                throw new FileLoadException("Could not read config file.", path);
            }
            var entryPointAttr = doc.Document.Root.Attribute("entryPoint");
            if (entryPointAttr != null)
            {
                var entryPoint = entryPointAttr.Value;
                if (!string.IsNullOrWhiteSpace(entryPoint))
                {
                    Configuration.EntryPoint = entryPoint;
                }
            }

            LoadPaths(doc.Document.Root);
            LoadBundles(doc.Document.Root);
        }

        private void LoadPaths(XElement docRoot)
        {
            var pathsElement = docRoot.Document.Root.Descendants("paths").FirstOrDefault();
            if (pathsElement != null)
            {
                var paths = pathsElement.Descendants("path").Select(r => new
                {
                    Key = r.Attribute("key").Value,
                    Value = r.Attribute("value").Value
                });
                foreach (var scriptPath in paths)
                {
                    if (!Configuration.Paths.ContainsKey(scriptPath.Key))
                    {
                        Configuration.Paths.Add(scriptPath.Key, scriptPath.Value);
                    }
                }
            }
        }

        private void LoadBundles(XElement docRoot)
        {
            var bundlesElement = docRoot.Document.Root.Descendants("bundles").FirstOrDefault();
            if (bundlesElement != null)
            {
                var bundles = bundlesElement.Descendants("bundle").Select(r => new BundleDefinition
                {
                    Name = r.Attribute("name").Value,
                    IsVirtual = ReadBooleanAttribute(r.Attribute("virtual")),
                    OutputPath = ReadStringAttribute(r.Attribute("outputPath")),
                    Includes = ReadStringListAttribute(r.Attribute("includes")),
                    Items = r.Descendants("bundleItem").Select(x => new BundleItem
                    {
                        ModuleName = x.Attribute("path").Value,
                        CompressionType = ReadStringAttribute(x.Attribute("compression"))
                    }).ToList()
                });

                foreach (var bundle in bundles)
                {
                    if (!Configuration.Bundles.Where(r => r.Name == bundle.Name).Any())
                    {
                        Configuration.Bundles.Add(bundle);
                    }
                }
            }
        }

        private List<string> ReadStringListAttribute(XAttribute attribute)
        {
            if (attribute == null)
            {
                return new List<string>();
            }
            var result = attribute.Value.Split(',').Select(r => r.Trim()).Distinct().ToList();
            return result;
        }

        private string ReadStringAttribute(XAttribute attribute)
        {
            if (attribute == null)
            {
                return string.Empty;
            }
            return attribute.Value;
        }

        private bool ReadBooleanAttribute(XAttribute attribute)
        {
            if (attribute == null)
            {
                return false;
            }
            return Convert.ToBoolean(attribute.Value);
        }

    }
}