# Called from C# through JNI by name, and implemented by C# through AndroidJavaProxy; keep both when the app is minified.
-keep class ai.hypa.nano.HypaNano { public static *; }
-keep interface ai.hypa.nano.HypaNano$TextListener { *; }
-keep interface ai.hypa.nano.HypaNano$DownloadListener { *; }
