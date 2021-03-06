﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Azure.WebJobs.Logging
{
    /// <summary>
    /// Publically visible. 
    /// Represent a function invocation. 
    /// </summary>
    public class FunctionInstanceLogItem : IFunctionInstanceBaseEntry
    {
        /// <summary>Gets or sets the function instance ID.</summary>
        public Guid FunctionInstanceId { get; set; }

        /// <summary>Gets or sets the Function ID of the ancestor function instance.</summary>
        public Guid? ParentId { get; set; }

        /// <summary>Name of this function. This can be used in querying other instances of this function. </summary>
        public string FunctionName { get; set; }

        /// <summary>
        /// An optional hint about why this function was invoked. It may have been triggered, replayed, manually invoked, etc. 
        /// </summary>
        public string TriggerReason { get; set; }

        /// <summary>UTC time that the function started executing.</summary>
        public DateTime StartTime { get; set; }

        /// <summary>If set, the time the function completed (either successfully or failure). </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Current status of this instance. 
        /// </summary>
        public FunctionInstanceStatus Status { get; set; }

        /// <summary>
        /// Null on success.
        /// Else, set to some string with error details. 
        /// </summary>
        public string ErrorDetails { get; set; }

        /// <summary>Gets or sets the function's argument values and help strings.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public IDictionary<string, string> Arguments { get; set; }

        // Direct inline capture for small log outputs. For large log outputs, this is faulted over to a blob. 
        /// <summary></summary>
        public string LogOutput { get; set; }

        /// <summary>
        /// Get a summary for this instance. 
        /// </summary>
        /// <returns></returns>
        public string GetDisplayTitle()
        {
            IEnumerable<string> argumentValues = null;
            if (this.Arguments != null)
            {
                argumentValues = this.Arguments.Values;
            }

            return BuildFunctionDisplayTitle(this.FunctionName, argumentValues);
        }

        /// <summary>
        /// Helper to build an instance summary display name given the arguments. 
        /// </summary>
        /// <param name="functionName">name of the function</param>
        /// <param name="argumentValues">argument values for this instance</param>
        /// <returns></returns>
        public static string BuildFunctionDisplayTitle(string functionName, IEnumerable<string> argumentValues)
        {
            var name = new StringBuilder(functionName);
            if (argumentValues != null)
            {
                string parametersDisplayText = String.Join(", ", argumentValues);
                if (parametersDisplayText != null)
                {
                    // Remove newlines to avoid 403/forbidden storage exceptions when saving display title to blob metadata
                    // for function indexes. Newlines may be present in JSON-formatted arguments.
                    parametersDisplayText = parametersDisplayText.Replace("\r\n", String.Empty);

                    name.Append(" (");
                    if (parametersDisplayText.Length > 20)
                    {
                        name.Append(parametersDisplayText.Substring(0, 18))
                            .Append(" ...");
                    }
                    else
                    {
                        name.Append(parametersDisplayText);
                    }
                    name.Append(")");
                }
            }

            return name.ToString();
        }

        /// <summary>
        /// Validate the fields are throw if they are inconsistent. 
        /// </summary>
        public void Validate()
        {
            if (this.Status == FunctionInstanceStatus.Unknown)
            {
                this.Status = InferStatus(); 
            }

            if (this.FunctionInstanceId == Guid.Empty)
            {
                throw new InvalidOperationException("Function Instance Id must be set.");
            }

            if (string.IsNullOrWhiteSpace(this.FunctionName))
            {
                throw new InvalidOperationException("Function Name must be set.");
            }

            bool completedStatus = this.Status.IsCompleted();
            if (this.EndTime.HasValue)
            {
                if (!completedStatus)
                {
                    throw new InvalidOperationException("End Time must be null for a function with non-completed status '" + completedStatus + "'.");
                }
                if (this.StartTime > this.EndTime)
                {
                    throw new InvalidOperationException("End Time must be greater than start time");
                }
            }
            else
            {
                if (completedStatus)
                {
                    throw new InvalidOperationException("End Time must be null for a completed-status '" + completedStatus + "'.");
                }
            }

            bool hasSuccessStatus = this.Status == FunctionInstanceStatus.CompletedSuccess;
                
            if (this.ErrorDetails != null)
            {
                if (hasSuccessStatus)
                {
                    throw new InvalidOperationException("Status and Error Details are inconsistent.");
                }
            }
        }

        private FunctionInstanceStatus InferStatus()
        {
            if (!this.EndTime.HasValue)
            {
                return FunctionInstanceStatus.Running;
            }
            else {
                if (this.ErrorDetails == null)
                {
                    return FunctionInstanceStatus.CompletedSuccess;
                }
                else
                {
                    return FunctionInstanceStatus.CompletedFailure;
                }
            }
        }
    }

    /// <summary>
    /// Extension methods.
    /// </summary>
    public static class FunctionInstanceLogItemExtensions
    {
        /// <summary>
        /// true if this function has completed (either success or failure)
        /// </summary>
        public static bool IsCompleted(this FunctionInstanceLogItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }
            return item.EndTime.HasValue;
        }

        /// <summary>true if this function succeeded.</summary>
        public static bool IsSucceeded(this FunctionInstanceLogItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }
            return item.Status == FunctionInstanceStatus.CompletedSuccess;
        }
    }
}