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

      if (References == null || References.Length == 0)
      {
        return true;
      }

      if (OutputPath == null || string.IsNullOrEmpty(OutputPath.ItemSpec))
      {
        throw new ArgumentException("Task parameter 'OutputPath' not provided.", nameof(OutputPath));
      }

      var outputDirectory = OutputPath.ItemSpec;

      var signedAssemblyFolder = Path.GetFullPath(Path.Combine(outputDirectory, "StrongNameSigner"));
      if (!Directory.Exists(signedAssemblyFolder))
      {
        Directory.CreateDirectory(signedAssemblyFolder);
      }

      // ReSharper disable once AssignNullToNotNullAttribute
      var snkFilePath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location),
        "StrongNameSigner.snk");
      if (!File.Exists(snkFilePath))
      {
        File.WriteAllBytes(snkFilePath, SigningHelper.GenerateStrongNameKeyPair());
      }

      Log.LogMessage(MessageImportance.Normal, "Signed Assembly Directory: {0}", signedAssemblyFolder);
      Log.LogMessage(MessageImportance.Normal, "SNK File Path: {0}", snkFilePath);

      var initialReferences = ReadReferences(References)
        .ToList();

      var probingPaths = initialReferences.Select(r => Path.GetDirectoryName(r.TaskItem.ItemSpec))
        .Distinct()
        .ToArray();

      var initiallySignedAssemblies = initialReferences
        .Where(x => x.AssemblyInfo.IsSigned)
        .ToList();

      var signedAssemblies = initialReferences
        .Where(x => !x.AssemblyInfo.IsSigned)
        .Select(x => new
        {
          InitialReference = x,
          NewReference = CreateSignedReference(x, snkFilePath, outputDirectory, probingPaths)
        })
        .ToList();

      var finalReferences = initiallySignedAssemblies
        .Concat(signedAssemblies.Select(x => x.NewReference))
        .ToList();

      if (signedAssemblies.Any())
      {
        var assemblies = new HashSet<string>(finalReferences.Select(x => x.AssemblyInfo.FilePath));
        foreach (var assembly in assemblies)
        {
          var otherAssemblies = assemblies.Where(r => !r.Equals(assembly));
          foreach (var otherAssembly in otherAssemblies)
          {
            SigningHelper.FixAssemblyReference(assembly, otherAssembly, snkFilePath, null, probingPaths);
          }
        }

        // Remove all InternalsVisibleTo attributes without public keys from the processed assemblies. Signed assemblies cannot have unsigned friend assemblies.
        foreach (var filePath in new HashSet<string>(
          signedAssemblies.Select(x => x.NewReference.AssemblyInfo.FilePath), StringComparer.OrdinalIgnoreCase))
        {
          RemoveInvalidFriendAssemblyReferences(filePath, snkFilePath, probingPaths);
        }
      }

      // update '@(ReferenceCopyLocalPaths)' items
      if (CopyLocalPaths != null)
      {
        // key = old reference path, value = new reference path
        var changedPaths = signedAssemblies
          .ToDictionary(
            x => x.InitialReference.AssemblyInfo.FilePath,
            x => x.NewReference.AssemblyInfo.FilePath
          );

        NewCopyLocalFiles = ProcessCopyLocalPaths(CopyLocalPaths, changedPaths)
          .ToArray();
      }

      SignedAssembliesToReference = finalReferences
        .Select(x => x.TaskItem)
        .ToArray();

      return true;
    }

    /// <summary>
    /// Reads <see cref="ITaskItem"/> references, returns an instance of <see cref="ReferenceInfo"/> for each item in <paramref name="references"/>.
    /// </summary>
    /// <param name="references"></param>
    /// <returns></returns>
    protected virtual IEnumerable<ReferenceInfo> ReadReferences(IEnumerable<ITaskItem> references)
    {
      return references.Select(x => new ReferenceInfo(x, SigningHelper.GetAssemblyInfo(x.ItemSpec)));
    }

    /// <summary>
    /// Creates signed reference fot <paramref name="reference"/>.
    /// Signs the assembly with the <paramref name="snkFilePath"/>, places into <paramref name="outputDirectory"/> and
    /// returns an instance of <see cref="ReferenceInfo"/> pointing at the assembly created.
    /// </summary>
    /// <param name="reference"></param>
    /// <param name="snkFilePath"></param>
    /// <param name="outputDirectory"></param>
    /// <param name="probingPaths"></param>
    /// <returns></returns>
    protected virtual ReferenceInfo CreateSignedReference(ReferenceInfo reference,
      string snkFilePath, string outputDirectory, params string[] probingPaths)
    {
      var signedAssembly =
        SignSingleAssembly(reference.AssemblyInfo.FilePath, snkFilePath, outputDirectory, probingPaths);

      // the same task item, but the path (ItemSpec) points to the signed assembly
      return new ReferenceInfo(new TaskItem(reference.TaskItem)
      {
        ItemSpec = signedAssembly.FilePath
      }, signedAssembly);
    }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    // ReSharper disable once MemberCanBePrivate.Global
    protected virtual IEnumerable<ITaskItem> ProcessCopyLocalPaths(IEnumerable<ITaskItem> copyLocalPaths,
      IDictionary<string, string> pathsToReplace)
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
