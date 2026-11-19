param([int]$ProcId, [string[]]$Items, [string]$Out)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class CW { public delegate bool P(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l); [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n); [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r); public struct R{public int L,T,Rt,B;}
 public static List<string> Get(uint pid){ var r=new List<string>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)){ var sb=new StringBuilder(256); GetWindowText(h,sb,256); r.Add(h.ToInt64()+"|"+sb);} return true;},IntPtr.Zero); return r; } }
"@
$A=[System.Windows.Automation.AutomationElement]
function Wins { [CW]::Get($ProcId) | ForEach-Object { $p=$_.Split("|",2); [pscustomobject]@{H=[int64]$p[0];T=$p[1]} } }
function Meas($h,$tag){
  $r=New-Object CW+R; [CW]::GetWindowRect([IntPtr]$h,[ref]$r)|Out-Null; $W=$r.Rt-$r.L
  $root=$A::FromHandle([IntPtr]$h)
  $els=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  $minL=99999;$minR=99999;$n=0
  foreach($e in $els){ try{ $c=$e.Current; if($c.IsOffscreen){continue}; if($c.AutomationId -match "^PART_(Line|Page)"){continue}; <# 2026-11-19: кнопки полосы прокрутки — не содержимое #> $t=$c.ControlType.ProgrammaticName; if($t -notmatch "Button|Edit|Text|ComboBox|CheckBox|RadioButton|DataItem|ListItem|TabItem"){continue}; $b=$c.BoundingRectangle; if($b.Width -le 2 -or $b.Width -gt $W*0.97){continue}; $l=[int]($b.Left-$r.L); $rt=[int]($r.Rt-$b.Right); if($l -lt $minL){$minL=$l}; if($rt -lt $minR){$minR=$rt}; $n++ }catch{} }
  $flag= if($n -eq 0){"?"} elseif($minL -lt 12 -or $minR -lt 12){"ТЕСНО"} elseif([math]::Abs($minL-$minR) -gt 14){"НЕСИММЕТРИЧНО"} else {"ok"}
  $name=($tag -replace '[^\wА-Яа-я]','_'); $bmp=New-Object System.Drawing.Bitmap $W,($r.B-$r.T); $g=[System.Drawing.Graphics]::FromImage($bmp); $dc=$g.GetHdc(); [CW]::PrintWindow([IntPtr]$h,$dc,2)|Out-Null; $g.ReleaseHdc($dc); $bmp.Save("$Out\$name.png"); $bmp.Dispose()
  "{0,-34} W={1,4} left={2,4} right={3,4} {4}" -f $tag,$W,$minL,$minR,$flag
}
New-Item -ItemType Directory -Force $Out | Out-Null
$main=(Wins | Where-Object { $_.T -like "Касса*" } | Select-Object -First 1).H
$mr=$A::FromHandle([IntPtr]$main)
foreach($n in @("Меню","Настройки")){ $e=$mr.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,$n))); $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 3 }
$sw=(Wins | Where-Object { $_.T -like "Настройки*" } | Select-Object -First 1).H
$sr=$A::FromHandle([IntPtr]$sw)
foreach($id in @("NavScales","NavPrint","NavScreen","NavMonitor","NavUpdates","NavOperations","NavAccount","NavEmployees","NavKeys")){ $b=$sr.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,$id))); if($b){ $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Meas $sw ("settings "+$id) } }
try{ $sr.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }catch{}
