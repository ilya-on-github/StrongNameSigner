using System;
using Microsoft.Build.Framework;

namespace Brutal.Dev.StrongNameSigner
{
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
  }
}