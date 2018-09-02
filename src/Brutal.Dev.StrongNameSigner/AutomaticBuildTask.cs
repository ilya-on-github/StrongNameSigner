using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Brutal.Dev.StrongNameSigner
{
  public class AutomaticBuildTask : Task
  {
    [Required]
    public ITaskItem[] References { get; set; }

    [Required]
    public ITaskItem OutputPath { get; set; }

    public ITaskItem[] CopyLocalPaths { get; set; }

    [Output]
    public ITaskItem[] SignedAssembliesToReference { get; set; }

    [Output]
    public ITaskItem[] NewCopyLocalFiles { get; set; }

    public override bool Execute()
    {
      var chagesMade = false;

      try
      {
        if (References == null || References.Length == 0)
        {
          return true;
        }

        if (OutputPath == null || string.IsNullOrEmpty(OutputPath.ItemSpec))
        {
          throw new ArgumentException("Task parameter 'OutputPath' not provided.", nameof(OutputPath));
        }

        var initialReferences = References;
        var newReferences = new List<ITaskItem>();

        var outputPath = OutputPath.ItemSpec;

        var signedAssemblyFolder = Path.GetFullPath(Path.Combine(outputPath, "StrongNameSigner"));
        if (!Directory.Exists(signedAssemblyFolder))
        {
          Directory.CreateDirectory(signedAssemblyFolder);
        }

        var snkFilePath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location),
          "StrongNameSigner.snk");
        if (!File.Exists(snkFilePath))
        {
          File.WriteAllBytes(snkFilePath, SigningHelper.GenerateStrongNameKeyPair());
        }

        Log.LogMessage(MessageImportance.Normal, "Signed Assembly Directory: {0}", signedAssemblyFolder);
        Log.LogMessage(MessageImportance.Normal, "SNK File Path: {0}", snkFilePath);

        // key = old reference path, value = new reference path
        var updatedReferencePaths = new Dictionary<string, string>();
        // 
        var signedAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 
        var probingPaths = initialReferences.Select(r => Path.GetDirectoryName(r.ItemSpec)).Distinct()
          .ToArray();

        foreach (var initialReference in initialReferences)
        {
          var referencedAssemblyInfo = SigningHelper.GetAssemblyInfo(
            // ReSharper disable once ArgumentsStyleNamedExpression
            assemblyFilePath: initialReference.ItemSpec
          );

          if (!referencedAssemblyInfo.IsSigned)
          {
            var signedAssemblyInfo =
              SignSingleAssembly(initialReference.ItemSpec, snkFilePath, signedAssemblyFolder,
                probingPaths);

            signedAssemblyPaths.Add(signedAssemblyInfo.FilePath);

            newReferences.Add(
              new TaskItem(initialReference)
              {
                ItemSpec = signedAssemblyInfo.FilePath
              }
            );

            if (signedAssemblyInfo.FilePath != referencedAssemblyInfo.FilePath)
            {
              updatedReferencePaths[referencedAssemblyInfo.FilePath] = signedAssemblyInfo.FilePath;
            }

            chagesMade = true;
          }
          else
          {
            newReferences.Add(
              new TaskItem(initialReference)
            );
          }
        }

        if (chagesMade)
        {
          var referencedAssembliesPaths = newReferences.Select(x => x.ItemSpec).ToList();
          var references = new HashSet<string>(referencedAssembliesPaths, StringComparer.OrdinalIgnoreCase);
          foreach (var filePath in referencedAssembliesPaths)
          {
            // Go through all the references excluding the file we are working on.
            foreach (var referencePath in references.Where(r => !r.Equals(filePath)))
            {
              FixSingleAssemblyReference(filePath, referencePath, snkFilePath, probingPaths);
            }
          }

          // Remove all InternalsVisibleTo attributes without public keys from the processed assemblies. Signed assemblies cannot have unsigned friend assemblies.
          foreach (var filePath in signedAssemblyPaths)
          {
            RemoveInvalidFriendAssemblyReferences(filePath, snkFilePath, probingPaths);
          }
        }

        // update '@(ReferenceCopyLocalPaths)' items
        if (CopyLocalPaths != null)
        {
          NewCopyLocalFiles = ProcessCopyLocalPaths(CopyLocalPaths, updatedReferencePaths)
            .ToArray();
        }

        SignedAssembliesToReference = newReferences.ToArray();

        return true;
      }
      catch (Exception ex)
      {
        Log.LogErrorFromException(ex, true);
      }

      return false;
    }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    // ReSharper disable once MemberCanBePrivate.Global
    protected IEnumerable<ITaskItem> ProcessCopyLocalPaths(IEnumerable<ITaskItem> copyLocalPaths, IDictionary<string, string> pathsToReplace)
    {
      return copyLocalPaths.Select(x =>
      {
        var pathWasModified = pathsToReplace.TryGetValue(x.ItemSpec, out var updatedPath);
        if (pathWasModified)
        {
          return new TaskItem(x)
          {
            ItemSpec = updatedPath
          };
        }
        
        return new TaskItem(x);
      });
    }
    
    protected class SignerTaskResult
    {
      public SignerTaskResult(ITaskItem[] signedAssembliesToReference, ITaskItem[] newCopyLocalFiles)
      {
        SignedAssembliesToReference = signedAssembliesToReference;
        NewCopyLocalFiles = newCopyLocalFiles;
      }

      public ITaskItem[] SignedAssembliesToReference { get; }

      public ITaskItem[] NewCopyLocalFiles { get; }
    }

    protected virtual SignerTaskResult Sign(ITaskItem[] references, ITaskItem outputPath,
      ITaskItem[] copyLocalPaths)
    {
      throw new NotImplementedException();
    }

    private AssemblyInfo SignSingleAssembly(string assemblyPath, string keyPath, string outputDirectory,
      params string[] probingPaths)
    {
      try
      {
        Log.LogMessage(MessageImportance.Low, string.Empty);
        Log.LogMessage(MessageImportance.Low, "Strong-name signing '{0}'...", assemblyPath);

        var oldInfo = SigningHelper.GetAssemblyInfo(assemblyPath);
        var newInfo = SigningHelper.SignAssembly(assemblyPath, keyPath, outputDirectory, null, probingPaths);

        if (!oldInfo.IsSigned && newInfo.IsSigned)
        {
          Log.LogMessage(MessageImportance.Normal, "'{0}' was strong-name signed successfully.",
            newInfo.FilePath);

          return newInfo;
        }
        else
        {
          Log.LogMessage(MessageImportance.Low, "'{0}' already strong-name signed...", assemblyPath);
        }
      }
      catch (BadImageFormatException bife)
      {
        Log.LogWarningFromException(bife, true);
      }
      catch (Exception ex)
      {
        Log.LogErrorFromException(ex, true, true, null);
      }

      return null;
    }

    private void FixSingleAssemblyReference(string assemblyPath, string referencePath, string keyFile,
      params string[] probingPaths)
    {
      try
      {
        Log.LogMessage(MessageImportance.Low, string.Empty);
        Log.LogMessage(MessageImportance.Low, "Fixing references to '{1}' in '{0}'...", assemblyPath,
          referencePath);

        if (SigningHelper.FixAssemblyReference(assemblyPath, referencePath, keyFile, null, probingPaths))
        {
          Log.LogMessage(MessageImportance.Normal, "References to '{1}' in '{0}' were fixed successfully.",
            assemblyPath, referencePath);
        }
        else
        {
          Log.LogMessage(MessageImportance.Low, "No assembly references to fix in '{0}'...", assemblyPath);
        }
      }
      catch (BadImageFormatException bife)
      {
        Log.LogWarningFromException(bife, true);
      }
      catch (Exception ex)
      {
        Log.LogErrorFromException(ex, true, true, null);
      }
    }

    private void RemoveInvalidFriendAssemblyReferences(string assemblyPath, string keyFile,
      params string[] probingPaths)
    {
      try
      {
        Log.LogMessage(MessageImportance.Low, string.Empty);
        Log.LogMessage(MessageImportance.Low, "Removing invalid friend references from '{0}'...", assemblyPath);

        if (SigningHelper.RemoveInvalidFriendAssemblies(assemblyPath, keyFile, null, probingPaths))
        {
          Log.LogMessage(MessageImportance.Normal,
            "Invalid friend assemblies removed successfully from '{0}'.",
            assemblyPath);
        }
        else
        {
          Log.LogMessage(MessageImportance.Low, "No friend references to fix in '{0}'...", assemblyPath);
        }
      }
      catch (BadImageFormatException bife)
      {
        Log.LogWarningFromException(bife, true);
      }
      catch (Exception ex)
      {
        Log.LogErrorFromException(ex, true, true, null);
      }
    }
  }
}
