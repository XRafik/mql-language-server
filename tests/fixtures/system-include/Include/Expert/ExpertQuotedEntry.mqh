// ExpertQuotedEntry.mqh — synthetic quoted parent-relative entry header (issue #186).
//
// Project-authored test fixture. Consumer documents include THIS header with
// an angle-bracket directive (#include <Expert\ExpertQuotedEntry.mqh>); its
// single nested hop uses the QUOTED parent-relative form, mirroring how real
// library headers reach a sibling directory of the Include tree with the
// quoted form. The hop below is exactly what the include closure must admit:
// "..\Collections\ArrayLongLib.mqh" resolves inside the same declared system
// include root, so CArrayLongSynth must be indexed from here exactly as it is
// from the angle-form chain.
#include "..\Collections\ArrayLongLib.mqh"
