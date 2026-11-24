// UTF-8 out, so the doctor's messages (and the skill, with --print) reach a Windows console intact
// rather than as the code page's approximation of them.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* redirected: nothing to set */ }
return CupriFace.Tool.Cli.Run(args, Console.Out, Console.Error);
