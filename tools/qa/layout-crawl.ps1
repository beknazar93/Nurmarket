param([int]$ProcId, [string[]]$Items, [string]$Out)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class CW { public delegate bool P(IntPtr h, IntPtr l); [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l); [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n); [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); public struct PT{public int X,Y;} [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref PT p);
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
$main=(Wins | Where-Object { $_.T -like "Касса*" -or $_.T -like "NurCRM*" } | Select-Object -First 1).H
$known=@{}; Wins | ForEach-Object { $known[$_.H]=1 }
foreach($it in $Items){
  try{
    $mr=$A::FromHandle([IntPtr]$main)
    $find={ param($n) $mr.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,$n))) }
    $path=$it.Split(">")
    for($si=0;$si -lt $path.Count;$si++){ $step=$path[$si]; $e=& $find $step.Trim(); if(-not $e){ "{0,-34} НЕ НАЙДЕНО: {1}" -f $it,$step; break }
      # 2026-11-19: последний шаг (кнопка, открывающая окно) — щелчком-сообщением окну кассы по hwnd, а не UIA Invoke: окна через PosDialogHost.Show
      # (синхронная модалка во вложенном цикле) не возвращали Invoke до закрытия, и дерево UIA окна было недоступно («Фильтр» висел 4 минуты).
      if($si -eq $path.Count-1){ $b=$e.Current.BoundingRectangle; $p=New-Object CW+PT; $p.X=[int]($b.Left+$b.Width/2); $p.Y=[int]($b.Top+$b.Height/2); [CW]::ScreenToClient([IntPtr]$main,[ref]$p)|Out-Null; $lp=[IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF))
        [CW]::PostMessage([IntPtr]$main,0x0200,[IntPtr]0,$lp)|Out-Null; Start-Sleep -Milliseconds 60; [CW]::PostMessage([IntPtr]$main,0x0201,[IntPtr]1,$lp)|Out-Null; Start-Sleep -Milliseconds 60; [CW]::PostMessage([IntPtr]$main,0x0202,[IntPtr]0,$lp)|Out-Null }
      else { try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{ try{ $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }catch{} } }
      Start-Sleep -Milliseconds 2200 }
    $new=Wins | Where-Object { -not $known.ContainsKey($_.H) }
    if($new){ foreach($w in $new){ Meas $w.H ($it+" ["+$w.T+"]"); try{ ($A::FromHandle([IntPtr]$w.H)).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }catch{} ; Start-Sleep -Milliseconds 700 } }
    else { Meas $main ($it+" [main]") }
    $cl=$mr.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,"Закрыть"))); if($cl){ try{ $cl.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{} }; Start-Sleep -Milliseconds 800
  }catch{ "{0,-34} ОШИБКА {1}" -f $it,$_.Exception.Message }
}
