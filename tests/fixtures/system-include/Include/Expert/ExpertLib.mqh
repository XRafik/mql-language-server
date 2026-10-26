// ExpertLib.mqh — synthetic includer header (issue #179, S5).
//
// Project-authored test fixture. One hop below the entry header, it pulls the
// collection header from its own directory family, mirroring how real
// library headers depend on headers from another directory of the Include
// tree. This hop is what makes the include closure transitive (depth > 1).
#include <Collections\ArrayLongLib.mqh>
