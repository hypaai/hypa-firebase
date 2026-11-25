import Foundation
// Drives the G07 plugin exactly as C# does: register the callback, read the status, ask, stream, cancel.
var finished = Set<Int32>()

@main struct Harness {
    static func main() {
        HypaFM_SetCallback { id, kind, text in
            let s = text.map { String(cString: $0) } ?? ""
            print("[\(id)] \(["chunk", "done", "error"][Int(kind)]): \(s.debugDescription)")
            if kind != 0 { finished.insert(id) }
        }
        let status = HypaFM_Status()
        print("status:", status, ["available", "downloading", "device not eligible", "Apple Intelligence off", "OS too old"][Int(status)])
        "In one sentence, what is a story?".withCString { HypaFM_Start(1, $0, false) }
        "Count from one to five in Spanish.".withCString { HypaFM_Start(2, $0, true) }
        "Write a long poem about the sea.".withCString { HypaFM_Start(3, $0, true) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { HypaFM_Cancel(3) }
        let start = Date()
        while finished.count < 3 && Date().timeIntervalSince(start) < 90 { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
        print("finished:", finished.sorted())
    }
}
