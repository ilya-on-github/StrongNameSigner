using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Framework;

namespace Brutal.Dev.StrongNameSigner
{
  // ReSharper disable once UnusedMember.Global
  public class LoggingSignerBuildTask : AutomaticBuildTask
  {
    public override bool Execute()
    {
      try
      {
        Log.LogMessage(MessageImportance.Normal, "---- Brutal Developer .NET Assembly Strong-Name Signer ----");

        return base.Execute();
      }
      catch (Exception e)
      {
        Log.LogErrorFromException(e);

        throw;
      }
    }

    protected override IEnumerable<ReferenceInfo> ReadReferences(IEnumerable<ITaskItem> references)
    {
      try
      {
        var referencesParsed = base.ReadReferences(references)
            .ToList();

        Log.LogMessage(string.Join($"{Environment.NewLine}",
          referencesParsed.Select((x, i) =>
            $"#{i}, signed: {x.AssemblyInfo.IsSigned}, path: {x.TaskItem.ItemSpec}")));

        return referencesParsed;
      }
      catch (Exception e)
      {
        Log.LogErrorFromException(e);

        throw;
      }
    }

    protected override ReferenceInfo CreateSignedReference(ReferenceInfo reference, string snkFilePath,
      string outputDirectory, params string[] probingPaths)
    {
      try
      {
        Log.LogMessage(
          $"Processing reference: {reference.TaskItem.ItemSpec}, signed: ${reference.AssemblyInfo.IsSigned}.");

        var newReference = base.CreateSignedReference(reference, snkFilePath, outputDirectory, probingPaths);

        Log.LogMessage(MessageImportance.Normal,
          $"Reference processed: '{newReference.TaskItem.ItemSpec}', signed: {newReference.AssemblyInfo.FilePath}.");

        return newReference;
      }
      catch (Exception e)
      {
        Log.LogErrorFromException(e);

        throw;
      }
    }
  }
}
