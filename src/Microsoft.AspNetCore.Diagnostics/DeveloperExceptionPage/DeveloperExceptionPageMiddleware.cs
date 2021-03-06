// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.RazorViews;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.StackTrace.Sources;

namespace Microsoft.AspNetCore.Diagnostics
{
    /// <summary>
    /// Captures synchronous and asynchronous exceptions from the pipeline and generates HTML error responses.
    /// </summary>
    public class DeveloperExceptionPageMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly DeveloperExceptionPageOptions _options;
        private readonly ILogger _logger;
        private readonly IFileProvider _fileProvider;
        private readonly System.Diagnostics.DiagnosticSource _diagnosticSource;
        private readonly ExceptionDetailsProvider _exceptionDetailsProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeveloperExceptionPageMiddleware"/> class
        /// </summary>
        /// <param name="next"></param>
        /// <param name="options"></param>
        /// <param name="loggerFactory"></param>
        /// <param name="hostingEnvironment"></param>
        /// <param name="diagnosticSource"></param>
        public DeveloperExceptionPageMiddleware(
            RequestDelegate next,
            IOptions<DeveloperExceptionPageOptions> options,
            ILoggerFactory loggerFactory,
            IHostingEnvironment hostingEnvironment,
            System.Diagnostics.DiagnosticSource diagnosticSource)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;
            _options = options.Value;
            _logger = loggerFactory.CreateLogger<DeveloperExceptionPageMiddleware>();
            _fileProvider = _options.FileProvider ?? hostingEnvironment.ContentRootFileProvider;
            _diagnosticSource = diagnosticSource;
            _exceptionDetailsProvider = new ExceptionDetailsProvider(_fileProvider, _options.SourceCodeLineCount);
        }

        /// <summary>
        /// Process an individual request.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(0, ex, "An unhandled exception has occurred while executing the request");

                if (context.Response.HasStarted)
                {
                    _logger.LogWarning("The response has already started, the error page middleware will not be executed.");
                    throw;
                }

                try
                {
                    context.Response.Clear();
                    context.Response.StatusCode = 500;

                    await DisplayException(context, ex);

                    if (_diagnosticSource.IsEnabled("Microsoft.AspNetCore.Diagnostics.UnhandledException"))
                    {
                        _diagnosticSource.Write("Microsoft.AspNetCore.Diagnostics.UnhandledException", new { httpContext = context, exception = ex });
                    }

                    return;
                }
                catch (Exception ex2)
                {
                    // If there's a Exception while generating the error page, re-throw the original exception.
                    _logger.LogError(0, ex2, "An exception was thrown attempting to display the error page.");
                }
                throw;
            }
        }

        // Assumes the response headers have not been sent.  If they have, still attempt to write to the body.
        private Task DisplayException(HttpContext context, Exception ex)
        {
            var compilationException = ex as ICompilationException;
            if (compilationException != null)
            {
                return DisplayCompilationException(context, compilationException);
            }

            return DisplayRuntimeException(context, ex);
        }

        private Task DisplayCompilationException(
            HttpContext context,
            ICompilationException compilationException)
        {
            var model = new CompilationErrorPageModel
            {
                Options = _options,
            };

            foreach (var compilationFailure in compilationException.CompilationFailures)
            {
                var stackFrames = new List<StackFrameSourceCodeInfo>();
                var exceptionDetails = new ExceptionDetails
                {
                    StackFrames = stackFrames,
                    ErrorMessage = compilationFailure.FailureSummary,
                };
                var fileContent = compilationFailure
                    .SourceFileContent
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                foreach (var item in compilationFailure.Messages)
                {
                    var frame = new StackFrameSourceCodeInfo
                    {
                        File = compilationFailure.SourceFilePath,
                        Line = item.StartLine,
                        Function = string.Empty
                    };

                    _exceptionDetailsProvider.ReadFrameContent(frame, fileContent, item.StartLine, item.EndLine);
                    frame.ErrorDetails = item.Message;

                    stackFrames.Add(frame);
                }

                model.ErrorDetails.Add(exceptionDetails);
            }

            var errorPage = new CompilationErrorPage
            {
                Model = model
            };

            return errorPage.ExecuteAsync(context);
        }

        private Task DisplayRuntimeException(HttpContext context, Exception ex)
        {
            var request = context.Request;

            var model = new ErrorPageModel
            {
                Options = _options,
                ErrorDetails = _exceptionDetailsProvider.GetDetails(ex),
                Query = request.Query,
                Cookies = request.Cookies,
                Headers = request.Headers
            };

            var errorPage = new ErrorPage(model);
            return errorPage.ExecuteAsync(context);
        }
    }
}
