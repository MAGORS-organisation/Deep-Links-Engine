// Global usings for the cryptography module.
//
// Only namespaces that appear in almost every file are imported here: the shared kernel
// abstractions this project implements, the BCL cryptography primitives, and InvariantCulture,
// which SHARED-KERNEL §0 requires on every format and parse.

global using System.Globalization;
global using System.Security.Cryptography;

global using Dle.Domain.Crypto;
global using Dle.Domain.Primitives;
