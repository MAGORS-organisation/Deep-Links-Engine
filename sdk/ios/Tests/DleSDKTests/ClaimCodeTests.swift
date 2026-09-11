import XCTest
@testable import DleSDK

/// Mirrors `tests/Dle.UnitTests/Attribution/ClaimCodeTests.cs` case for case, so the SDK and
/// the engine agree on what a claim code is.
final class ClaimCodeTests: XCTestCase {
    func testAlphabetAndLengthMatchTheDomain() {
        XCTAssertEqual(DleClaimCode.alphabet, "ACDEFGHJKLMNPQRTUVWXY34679")
        XCTAssertEqual(DleClaimCode.alphabet.count, 26)
        XCTAssertEqual(DleClaimCode.length, 6)
        for homoglyph in "BIOSZ01258" {
            XCTAssertFalse(DleClaimCode.alphabet.contains(homoglyph), "\(homoglyph) must not be in the alphabet")
        }
    }

    func testNormalizeUpperCasesAndStripsSeparators() {
        let cases: [(String, String)] = [
            ("acdefg", "ACDEFG"),
            ("ACD-EFG", "ACDEFG"),
            (" ACD EFG ", "ACDEFG"),
            ("acd-efg", "ACDEFG"),
            ("A C-D E F G", "ACDEFG"),
            ("acd\tefg\n", "ACDEFG"),
            ("acd\u{00A0}efg", "ACDEFG"),
        ]
        for (raw, expected) in cases {
            XCTAssertEqual(DleClaimCode.normalize(raw), expected, "normalize(\(raw.debugDescription))")
        }
    }

    func testNormalizeDoesNotSubstituteCharacters() {
        // O is not rewritten to 0: neither is in the alphabet, and rewriting could turn a typo
        // into a different valid code.
        let normalized = DleClaimCode.normalize("ACDEFO")
        XCTAssertEqual(normalized, "ACDEFO")
        XCTAssertFalse(DleClaimCode.isWellFormed(normalized))
    }

    func testNormalizeOfSeparatorsOnlyIsEmpty() {
        XCTAssertEqual(DleClaimCode.normalize(""), "")
        XCTAssertEqual(DleClaimCode.normalize("   "), "")
        XCTAssertEqual(DleClaimCode.normalize("---"), "")
    }

    func testNormalizeIsIdempotent() {
        let once = DleClaimCode.normalize("acd-efg")
        XCTAssertEqual(once, DleClaimCode.normalize(once))
    }

    func testNormalizeKeepsCharactersWithoutASimpleUpperCase() {
        // `char.ToUpper` maps one UTF-16 unit to one unit; ß has no such mapping and stays.
        XCTAssertEqual(DleClaimCode.normalize("aß"), "Aß")
    }

    func testIsWellFormed() {
        let cases: [(String?, Bool)] = [
            ("ACDEFG", true),
            ("34679A", true),
            ("XYWVUT", true),
            ("ABCDEF", false), // B is not in the alphabet
            ("ACDEF0", false), // zero is not in the alphabet
            ("ACDEF1", false),
            ("ACDEF8", false),
            ("acdefg", false), // must be normalised first
            ("ACDEF", false), // too short
            ("ACDEFGH", false), // too long
            ("", false),
            (nil, false),
            ("ACDÉFG", false),
        ]
        for (code, expected) in cases {
            XCTAssertEqual(DleClaimCode.isWellFormed(code), expected, "isWellFormed(\(String(describing: code)))")
        }
    }

    func testEveryAlphabetCharacterFormsAValidCode() {
        for character in DleClaimCode.alphabet {
            XCTAssertTrue(DleClaimCode.isWellFormed(String(repeating: character, count: 6)))
        }
    }
}
