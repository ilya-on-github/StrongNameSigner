using Microsoft.Build.Framework;

namespace Brutal.Dev.StrongNameSigner
{
  public class ReferenceInfo
  {
    public ReferenceInfo(ITaskItem taskItem, AssemblyInfo assemblyInfo)
    {
      TaskItem = taskItem;
      AssemblyInfo = assemblyInfo;
    }

    public ITaskItem TaskItem { get; }
    public AssemblyInfo AssemblyInfo { get; }
  }
}
