// This file is part of Hangfire.
// Copyright © 2016 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

#pragma warning disable 1591

using Hangfire.Logging;
using NLog;

namespace Meshmakers.Octo.Backend.BotServices.Hangfire;

public class NLogProvider : ILogProvider
{
    public ILog GetLogger(string name)
    {
        return new NLogWrapper(LogManager.GetLogger(name));
    }
}
