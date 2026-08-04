// MIT License
//
// config.js
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// Runtime configuration seam for a STANDALONE F8 Studio deployment (feature standalone-ui).
//
// This classic script is loaded FIRST in index.html, so window.__F8_CONFIG__ is set before the
// app module evaluates and the instance registry reads it. `apiUrl` is the Fallen-8 REST data
// plane the Studio talks to; "" means same-origin (the all-in-one default, where the API's own
// wwwroot serves this SPA). The standalone nginx image's entrypoint rewrites THIS one file from
// the F8_API_URL environment variable at container start, so a single built artifact can be
// pointed at any Fallen-8 REST endpoint without a rebuild.
window.__F8_CONFIG__ = { apiUrl: "" };
