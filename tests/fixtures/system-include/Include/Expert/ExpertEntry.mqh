// ExpertEntry.mqh — synthetic entry header (issue #179, S5).
//
// Project-authored test fixture. Consumer documents include THIS header with
// an angle-bracket directive (#include <Expert\ExpertEntry.mqh>); every other
// fixture symbol must arrive through the transitive include closure reached
// from here: the trade classes via an angle hop to Trade\, and the
// collection type one hop further via the same-directory includer header
// (the same-directory quoted style real library headers use for their
// siblings).
#include <Trade\TradeLib.mqh>
#include "ExpertLib.mqh"
