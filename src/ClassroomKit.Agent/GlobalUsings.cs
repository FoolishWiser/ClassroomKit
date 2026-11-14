// 显式全局 using：WPF 的 XAML 编译临时工程不会带 ImplicitUsings 生成的全局 using，
// 所以这里手写一份，主编译与临时编译行为一致。
global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows.Forms;
