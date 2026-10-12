// mtlcheck <file.metal>... | <dir> : compile each file with the OS's runtime Metal compiler (MTLDevice.makeLibrary,
// what the Unity player uses for its MSL), in parallel; print failures, exit 1 if any failed. Text after
// "/* reflection" is ignored.
// Directory mode is incremental: results go to <dir>/mtlcheck-<OS build>.tsv (file name, ok|fail, first error
// line) and files already listed there are not compiled again.
import Foundation
import Metal

let dev = MTLCreateSystemDefaultDevice()!
let opts = MTLCompileOptions()
var args = Array(CommandLine.arguments.dropFirst())
var isDir: ObjCBool = false
var resultsPath: String? = nil
var known: [String: String] = [:]
if args.count == 1, FileManager.default.fileExists(atPath: args[0], isDirectory: &isDir), isDir.boolValue {
    let dir = args[0]
    let os = ProcessInfo.processInfo.operatingSystemVersionString.replacingOccurrences(of: " ", with: "_")
    resultsPath = "\(dir)/mtlcheck-\(os).tsv"
    if let text = try? String(contentsOfFile: resultsPath!, encoding: .utf8) {
        for line in text.split(separator: "\n") {
            let f = line.split(separator: "\t", maxSplits: 2).map(String.init)
            if f.count >= 2 { known[f[0]] = line.description }
        }
    }
    let files = try! FileManager.default.contentsOfDirectory(atPath: dir).filter { $0.hasSuffix(".metal") }
    let present = Set(files)
    known = known.filter { present.contains($0.key) }  // results of files that are gone don't count
    args = files.filter { known[$0] == nil }.sorted().map { "\(dir)/\($0)" }
}

let lock = NSLock()
var results: [String] = []
var failed = 0
DispatchQueue.concurrentPerform(iterations: args.count) { i in
    let path = args[i]
    var src = (try? String(contentsOfFile: path, encoding: .utf8)) ?? ""
    if let r = src.range(of: "/* reflection") { src = String(src[..<r.lowerBound]) }
    var line = "\((path as NSString).lastPathComponent)\tok\t"
    do { _ = try dev.makeLibrary(source: src, options: opts) }
    catch {
        let msg = error.localizedDescription
        let first = msg.split(separator: "\n").first(where: { $0.contains("error:") }).map(String.init) ?? String(msg.prefix(200))
        line = "\((path as NSString).lastPathComponent)\tfail\t\(first)"
        lock.lock(); failed += 1; if resultsPath == nil { print("FAIL \(path)\n\(msg.prefix(600))") }; lock.unlock()
    }
    lock.lock(); results.append(line); lock.unlock()
}
if let rp = resultsPath {
    let all = known.values.sorted() + results.sorted()
    try! (all.joined(separator: "\n") + "\n").write(toFile: rp, atomically: true, encoding: .utf8)
    let fails = all.filter { $0.contains("\tfail\t") }.count
    print("\(all.count - fails) ok, \(fails) failed (\(results.count) compiled now, \(known.count) from \(rp))")
    exit(fails == 0 ? 0 : 1)
}
print("\(args.count - failed) ok, \(failed) failed")
exit(failed == 0 ? 0 : 1)
